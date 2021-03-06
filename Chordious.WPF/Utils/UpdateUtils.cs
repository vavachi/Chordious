﻿// 
// UpdateUtils.cs
//  
// Author:
//       Jon Thysell <thysell@gmail.com>
// 
// Copyright (c) 2015, 2016, 2017 Jon Thysell <http://jonthysell.com>
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml;

using GalaSoft.MvvmLight.Messaging;

using com.jonthysell.Chordious.Core.ViewModel;

using com.jonthysell.Chordious.WPF.Resources;

namespace com.jonthysell.Chordious.WPF
{
    public class UpdateUtils
    {
        public static AppViewModel AppVM
        {
            get
            {
                return AppViewModel.Instance;
            }
        }

        public static bool IsCheckingforUpdate { get; private set; }

        public static Task UpdateCheckAsync(bool confirmUpdate, bool showUpToDate)
        {
            return Task.Factory.StartNew(() =>
            {
                UpdateCheck(confirmUpdate, showUpToDate);
            });
        }

        public static void UpdateCheck(bool confirmUpdate, bool showUpToDate)
        {
            try
            {
                IsCheckingforUpdate = true;

                List<InstallerInfo> installerInfos = GetLatestInstallerInfos();

                ReleaseChannel targetReleaseChannel = GetReleaseChannel();

                ulong maxVersion = LongVersion(AppVM.FullVersion);

                InstallerInfo latestVersion = null;

                bool updateAvailable = false;
                foreach (InstallerInfo installerInfo in installerInfos)
                {
                    if (installerInfo.ReleaseChannel == targetReleaseChannel)
                    {
                        ulong installerVersion = LongVersion(installerInfo.Version);

                        if (installerVersion > maxVersion)
                        {
                            updateAvailable = true;
                            latestVersion = installerInfo;
                            maxVersion = installerVersion;
                        }
                    }
                }

                LastUpdateCheck = DateTime.Now;

                if (updateAvailable)
                {
                    if (confirmUpdate)
                    {
                        string message = string.Format(Strings.ChordiousUpdateAvailableUpdateNowMessageFormat, latestVersion.Version);
                        AppVM.DoOnUIThread(() =>
                        {
                            Messenger.Default.Send(new ConfirmationMessage(message, (confirmed) =>
                            {
                                try
                                {
                                    if (confirmed)
                                    {
                                        Update(latestVersion);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    ExceptionUtils.HandleException(new UpdateException(ex));
                                }
                            }));
                        });
                    }
                    else
                    {
                        Update(latestVersion);
                    }
                }
                else
                {
                    if (showUpToDate)
                    {
                        AppVM.DoOnUIThread(() =>
                        {
                            Messenger.Default.Send(new ChordiousMessage(Strings.ChordiousUpdateNotAvailableMessage));
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionUtils.HandleException(new UpdateException(ex));
            }
            finally
            {
                IsCheckingforUpdate = false;
            }
        }

        private static void Update(InstallerInfo installerInfo)
        {
            if (null == installerInfo)
            {
                throw new ArgumentNullException("installerInfo");
            }

            if (!IsConnectedToInternet)
            {
                throw new UpdateNoInternetException();
            }

            string tempPath = Path.GetTempPath();

            string msiPath = Path.Combine(tempPath, "ChordiousSetup.msi");

            if (File.Exists(msiPath))
            {
                File.Delete(msiPath);
            }

            using (WebClient client = new WebClient())
            {
                client.Headers["User-Agent"] = _userAgent;
                client.DownloadFile(installerInfo.Url, msiPath);
            }

            string cmdFile = Path.Combine(tempPath, "UpdateChordious.cmd");

            using (StreamWriter sw = new StreamWriter(new FileStream(cmdFile, FileMode.Create)))
            {
                sw.WriteLine("msiexec /i \"{0}\" /qb", msiPath);
            }

            AppVM.DoOnUIThread(() =>
            {
                Process p = new Process();
                p.StartInfo = new ProcessStartInfo("cmd.exe", string.Format("/c {0}", cmdFile));
                p.StartInfo.CreateNoWindow = true;
                p.Start();

                App.Current.Shutdown();
            });
        }

        public static List<InstallerInfo> GetLatestInstallerInfos()
        {
            if (!IsConnectedToInternet)
            {
                throw new UpdateNoInternetException();
            }

            List<InstallerInfo> installerInfos = new List<InstallerInfo>();

            HttpWebRequest request = WebRequest.CreateHttp(_updateUrl);
            request.UserAgent = _userAgent;
            request.CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.NoCacheNoStore);

            using (XmlReader reader = XmlReader.Create(request.GetResponse().GetResponseStream()))
            {
                while (reader.Read())
                {
                    if (reader.IsStartElement())
                    {
                        if (reader.Name == "update")
                        {
                            string version = reader.GetAttribute("version");
                            string url = reader.GetAttribute("url");
                            ReleaseChannel releaseChannel = (ReleaseChannel)Enum.Parse(typeof(ReleaseChannel), reader.GetAttribute("channel"));
                            installerInfos.Add(new InstallerInfo(version, url, releaseChannel));
                        }
                    }
                }
            }

            return installerInfos;
        }

        public static ulong LongVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new ArgumentNullException("version");
            }

            ulong vers = 0;

            string[] parts = version.Trim().Split('.');

            for (int i = 0; i < parts.Length; i++)
            {
                vers |= (ulong.Parse(parts[i]) << ((4 - (i + 1)) * 16));
            }

            return vers;
        }

        public static ReleaseChannel GetReleaseChannel()
        {
            try
            {
                return (ReleaseChannel)Enum.Parse(typeof(ReleaseChannel), AppVM.GetSetting("app.releasechannel"));
            }
            catch (Exception) { }

            return ReleaseChannel.Official;
        }

        public static bool UpdateEnabled
        {
            get
            {
#if PORTABLE
                return false;
#else
                return true;
#endif
            }
        }

        public static bool CheckUpdateOnStart
        {
            get
            {
                try
                {
                    return bool.Parse(AppVM.GetSetting("app.checkupdateonstart"));
                }
                catch (Exception) { }

                return false;
            }
            set
            {
                AppVM.SetSetting("app.checkupdateonstart", value);
            }
        }

        public static bool IsConnectedToInternet
        {
            get
            {
                int Description;
                return NativeMethods.InternetGetConnectedState(out Description, 0);
            }
        }

        public static DateTime LastUpdateCheck
        {
            get
            {
                try
                {
                    return DateTime.Parse(AppVM.GetSetting("app.lastupdatecheck"), null, DateTimeStyles.AssumeUniversal);
                }
                catch (Exception) { }

                return DateTime.MinValue;
            }
            set
            {
                AppVM.SetSetting("app.lastupdatecheck", value.ToUniversalTime().ToString("s"));
            }
        }

        private const string _updateUrl = "http://update.chordious.com";
        private const string _userAgent = "Mozilla/5.0";
    }

    public class InstallerInfo
    {
        public string Version { get; private set; }

        public string Url { get; private set; }

        public ReleaseChannel ReleaseChannel { get; private set; }

        public InstallerInfo(string version, string url, ReleaseChannel releaseChannel)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new ArgumentNullException("version");
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentNullException("url");
            }

            Version = version.Trim();
            Url = url.Trim();
            ReleaseChannel = releaseChannel;
        }
    }

    public enum ReleaseChannel
    {
        Official,
        Preview
    }

    [Serializable]
    public class UpdateException : Exception
    {
        public override string Message
        {
            get
            {
                return Strings.ChordiousUpdateExceptionMessage;
            }
        }

        public UpdateException(Exception innerException) : base("", innerException) { }
    }

    [Serializable]
    public class UpdateNoInternetException : Exception
    {
        public override string Message
        {
            get
            {
                return Strings.ChordiousUpdateNoInternetExceptionMessage;
            }
        }

        public UpdateNoInternetException() : base() { }
    }

    internal static partial class NativeMethods
    {
        [DllImport("wininet.dll")]
        internal extern static bool InternetGetConnectedState(out int Description, int ReservedValue);
    }
}
