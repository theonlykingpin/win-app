﻿/*
 * Copyright (c) 2020 Proton Technologies AG
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
using System.IO;
using NUnit.Framework;
using ProtonVPN.UI.Test.ApiClient;
using ProtonVPN.UI.Test.TestsHelper;
using System.Reflection;

namespace ProtonVPN.UI.Test.Tests
{
    [SetUpFixture]
    public class SetUp : UITestSession
    {
        private readonly string _testRailUrl = "https://proton.testrail.io/";

        [OneTimeSetUp]
        public void TestInitialize()
        {
            string dir = Path.GetDirectoryName(typeof(SetUp).Assembly.Location);
            Directory.SetCurrentDirectory(dir);

            TestRailClient = new TestRailAPIClient(_testRailUrl,
                    TestUserData.GetTestrailUser().Username, TestUserData.GetTestrailUser().Password);
            if (!TestEnvironment.AreTestsRunningLocally())
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string path = Path.Combine(Path.GetDirectoryName(asm.Location), "ProtonVPN.exe");
                string version = Assembly.LoadFile(path).GetName().Version.ToString();
                string branchName = Environment.GetEnvironmentVariable("CI_COMMIT_BRANCH");
                version = version.Substring(0, version.Length - 2);
                if (!TestRailClient.ShouldUpdateRun())
                {
                    TestRailClient.CreateTestRun($"{branchName} {version} {DateTime.Now}");
                }
            }
        }
    }
}