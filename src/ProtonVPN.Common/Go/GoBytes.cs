﻿/*
 * Copyright (c) 2022 Proton Technologies AG
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
using System.Runtime.InteropServices;

namespace ProtonVPN.Common.Go
{
    public struct GoBytes
    {
        // Pointer to the UTF-8 encoded string buffer
        public IntPtr Data;

        // Length of the string buffer in bytes
        public IntPtr Length;

        // Total size of the buffer in bytes
        public IntPtr Capacity;
    }

    public struct DisposableGoBytes : IDisposable
    {
        // Pointer to the UTF-8 encoded string buffer
        public IntPtr Data;

        // Length of the string buffer in bytes
        public IntPtr Length;

        // Total size of the buffer in bytes
        public IntPtr Capacity;

        public void Dispose()
        {
            PInvoke.ZeroMemory(Data, Capacity.ToInt32());
            Marshal.FreeHGlobal(Data);
        }
    }
}