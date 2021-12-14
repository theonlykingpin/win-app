﻿/*
 * Copyright (c) 2021 Proton Technologies AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.Common;
using ProtonVPN.Common.Logging;
using ProtonVPN.Common.Threading;
using ProtonVPN.Common.Vpn;
using ProtonVPN.Vpn.Common;

namespace ProtonVPN.Vpn.Connection
{
    internal class ReconnectingWrapper : IVpnConnection
    {
        private readonly CancellationHandle _cancellationHandle = new();
        private readonly ILogger _logger;
        private readonly IVpnEndpointCandidates _candidates;
        private readonly IEndpointScanner _endpointScanner;
        private readonly ISingleVpnConnection _origin;

        private VpnState _state;
        private VpnConfig _config;
        private VpnCredentials _credentials;
        private VpnEndpoint _endpoint;
        private bool _isToConnect;
        private bool _isToReconnect;
        private bool _isToDiscardProtocol;

        public ReconnectingWrapper(
            ILogger logger,
            IVpnEndpointCandidates candidates,
            IEndpointScanner endpointScanner,
            ISingleVpnConnection origin)
        {
            _logger = logger;
            _candidates = candidates;
            _endpointScanner = endpointScanner;
            _origin = origin;

            _origin.StateChanged += Origin_StateChanged;
        }

        public event EventHandler<EventArgs<VpnState>> StateChanged;

        public InOutBytes Total => _origin.Total;

        public void Connect(IReadOnlyList<VpnHost> servers, VpnConfig config, VpnCredentials credentials)
        {
            _candidates.Set(servers);
            _candidates.Reset();
            _config = config;
            _credentials = credentials;
            _isToConnect = true;
            _isToReconnect = true;
            _isToDiscardProtocol = true;

            _logger.Info("[ReconnectingWrapper] Requesting disconnect as first step of connection process.");
            CancelTokenAndDisconnect(VpnError.NoneKeepEnabledKillSwitch);
        }

        public void UpdateAuthCertificate(string certificate)
        {
            _origin.UpdateAuthCertificate(certificate);
        }

        public void SetFeatures(VpnFeatures vpnFeatures)
        {
            _origin.SetFeatures(vpnFeatures);
        }

        public void Disconnect(VpnError error = VpnError.None)
        {
            _isToConnect = false;
            _isToReconnect = false;
            CancelTokenAndDisconnect(error);
        }

        private void CancelTokenAndDisconnect(VpnError error)
        {
            _logger.Info($"[ReconnectingWrapper] A disconnect was requested with error '{error}'.");
            _cancellationHandle.Cancel();
            _origin.Disconnect(error);
        }

        private void Origin_StateChanged(object sender, EventArgs<VpnState> e)
        {
            _state = e.Data;

            if (_state.Status == VpnStatus.Connecting || _state.Status == VpnStatus.Reconnecting)
            {
                _isToDiscardProtocol = true;
            }
            
            if (IsToHandleAdapterError())
            {
                OnAdapterError();
            } 
            else if (_state.Status == VpnStatus.Disconnected && _isToConnect)
            {
                _isToConnect = false;
                _logger.Info("[ReconnectingWrapper] A connect is pending. " +
                             "Starting connection after status changed to Disconnected.");
                PingAndConnectAsync();
            }
            else if (IsToCancelReconnection())
            {
                _isToReconnect = false;
            }
            else if (IsToReconnect())
            {
                ConnectToNextEndpoint();
            }

            OnStateChanged(FilterVpnState(_state));
        }

        private bool IsToHandleAdapterError()
        {
            return _isToDiscardProtocol && IsAdapterError(_state.Error) &&
                   (_state.Status == VpnStatus.Disconnecting || _state.Status == VpnStatus.Disconnected);
        }

        private void OnAdapterError()
        {
            _isToDiscardProtocol = false;
            if (_config.PreferredProtocols.Any())
            {
                _logger.Info($"[ReconnectingWrapper] Discarding protocol '{_config.PreferredProtocols[0]}'.");
                _config.PreferredProtocols.RemoveAt(0);
            }

            if (_config.PreferredProtocols.Count == 0)
            {
                _logger.Warn("[ReconnectingWrapper] Preferred protocols list is empty. Disconnecting.");
                CancelTokenAndDisconnect(VpnError.ServerUnreachable);
            }
            else
            {
                _logger.Info("[ReconnectingWrapper] Preferred protocols list is not empty. " +
                    "Reconnecting after discarding protocol.");
                _candidates.Reset();
                _isToConnect = true;
                _isToReconnect = true;
            }
        }

        private async Task PingAndConnectAsync()
        {
            _logger.Info("[ReconnectingWrapper] Probing servers.");
            CancellationToken cancellationToken = _cancellationHandle.Token;
            bool isResponding = false;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("[ReconnectingWrapper] Disconnection has been requested. Endpoint scanning stopped.");
                    break;
                }

                VpnEndpoint endpoint = _candidates.NextIp(_config);
                if (endpoint.IsEmpty)
                {
                    _logger.Info($"[ReconnectingWrapper] No more endpoints in the list.");
                    break;
                }

                isResponding = await IsEndpointRespondingAsync(endpoint, cancellationToken);
                if (isResponding)
                {
                    _logger.Info($"[ReconnectingWrapper] The server {endpoint.Server.Ip} has responded to the ping.");
                    break;
                }

                _logger.Info($"[ReconnectingWrapper] The server {endpoint.Server.Ip} " +
                             "has failed to respond to the ping.");
            }

            _candidates.Reset();
            HandleEndpointResponse(isResponding, cancellationToken);
        }

        private async Task<bool> IsEndpointRespondingAsync(VpnEndpoint endpoint, CancellationToken cancellationToken)
        {
            OnStateChanged(new VpnState(VpnStatus.Pinging, VpnError.None, string.Empty,
                endpoint.Server.Ip, _config.VpnProtocol, _config.OpenVpnAdapter, endpoint.Server.Label));

            VpnEndpoint bestEndpoint = await _endpointScanner.ScanForBestEndpointAsync(
                endpoint, _config.Ports, _config.PreferredProtocols, cancellationToken);
            return bestEndpoint.Port != 0;
        }

        private void HandleEndpointResponse(bool isResponding, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[ReconnectingWrapper] Disconnection has been requested. Connection canceled.");
                return;
            }

            if (isResponding)
            {
                _logger.Info("[ReconnectingWrapper] At least one server has responded to a ping. " +
                             "Attempting connections.");
                ConnectToNextEndpoint();
            }
            else
            {
                _logger.Info("[ReconnectingWrapper] No server has responded to a ping. Disconnecting.");
                Disconnect(VpnError.PingTimeoutError);
            }
        }

        private void ConnectToNextEndpoint()
        {
            _endpoint = _candidates.NextHost(_config);
            bool isEndpointAvailableToConnect = _endpoint?.Server != null && !_endpoint.Server.IsEmpty();
            if (isEndpointAvailableToConnect)
            {
                _logger.Info($"[ReconnectingWrapper] Next endpoint is {_endpoint.Server.Ip}/" +
                             $"{_endpoint.Server.Label}. Connecting.");
                _origin.Connect(_endpoint, _credentials, _config);
            }
            else
            {
                _isToReconnect = false;
                _logger.Info("[ReconnectingWrapper] No more VPN endpoints to try. Disconnecting.");
                Disconnect(_state.Error);
            }
        }

        private bool IsToCancelReconnection()
        {
            return !_isToConnect &&
                   (_state.Status == VpnStatus.Disconnecting || _state.Status == VpnStatus.Disconnected) &&
                   (!IsServerError(_state.Error) || !IsAdapterError(_state.Error));
        }

        private bool IsServerError(VpnError error)
        {
            return error == VpnError.NetshError ||
                   error == VpnError.TlsError ||
                   error == VpnError.PingTimeoutError ||
                   error == VpnError.Unknown;
        }

        private bool IsAdapterError(VpnError error)
        {
            return error == VpnError.ServerUnreachable || error == VpnError.AdapterTimeoutError;
        }

        private bool IsToReconnect()
        {
            return _isToReconnect &&
                (_state.Status == VpnStatus.Disconnecting || _state.Status == VpnStatus.Disconnected) &&
                IsServerError(_state.Error);
        }

        private VpnState FilterVpnState(VpnState state)
        {
            if (IsToSuppressVpnState(state))
            {
                return null;
            }

            if (ShouldBeReconnecting(state))
            {
                return CreateReconnectingVpnState(state);
            }

            return state;
        }

        private bool IsToSuppressVpnState(VpnState state)
        {
            return _isToReconnect &&
                   (state.Status == VpnStatus.Disconnecting || state.Status == VpnStatus.Disconnected);
        }

        private bool ShouldBeReconnecting(VpnState state)
        {
            return state.Status == VpnStatus.Connecting;
        }

        private VpnState CreateReconnectingVpnState(VpnState state)
        {
            return new(
                VpnStatus.Reconnecting,
                state.Error,
                state.LocalIp,
                state.RemoteIp,
                state.VpnProtocol,
                state.OpenVpnAdapter,
                state.Label);
        }

        private void OnStateChanged(VpnState state)
        {
            if (state != null)
            {
                StateChanged?.Invoke(this, new EventArgs<VpnState>(state));
            }
        }
    }
}