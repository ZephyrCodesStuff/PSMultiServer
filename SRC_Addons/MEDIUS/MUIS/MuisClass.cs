﻿using DotNetty.Common.Internal.Logging;
using Newtonsoft.Json;
using PSMultiServer.Addons.Medius.RT.Models;
using PSMultiServer.Addons.Medius.Server.Common;
using PSMultiServer.Addons.Medius.Server.Database;
using PSMultiServer.Addons.Medius.Server.Plugins;
using PSMultiServer.Addons.Medius.MUIS.Config;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using PSMultiServer.Addons.Medius.DME;

namespace PSMultiServer.Addons.Medius.MUIS
{
    public class MuisClass
    {
        private static string CONFIG_DIRECTIORY = "./loginformNtemplates/MUIS";
        public static string CONFIG_FILE => Path.Combine(CONFIG_DIRECTIORY, "muis.json");
        public static string DB_CONFIG_FILE => Path.Combine(CONFIG_DIRECTIORY, "db.config.json");

        static readonly IInternalLogger Logger = InternalLoggerFactory.GetInstance<MuisClass>();

        public static ServerSettings Settings = new ServerSettings();
        public static DbController Database = null;

        public static MediusManager Manager = new MediusManager();
        public static PluginsManager Plugins = null;

        public static RSA_KEY GlobalAuthPublic = null;

        public static MUIS[] UniverseInfoServers = null;

        private static Dictionary<int, AppSettings> _appSettings = new Dictionary<int, AppSettings>();
        private static AppSettings _defaultAppSettings = new AppSettings(0);
        private static Dictionary<string, int[]> _appIdGroups = new Dictionary<string, int[]>();
        private static ulong _sessionKeyCounter = 0;
        private static readonly object _sessionKeyCounterLock = _sessionKeyCounter;
        private static DateTime? _lastSuccessfulDbAuth = null;
        private static int _ticks = 0;
        private static Stopwatch _sw = new Stopwatch();
        private static Timer.HighResolutionTimer _timer;

        static async Task TickAsync()
        {



            // Attempt to authenticate with the db middleware
            // We do this every 24 hours to get a fresh new token
            if ((_lastSuccessfulDbAuth == null || (Utils.GetHighPrecisionUtcTime() - _lastSuccessfulDbAuth.Value).TotalHours > 24))
            {
                if (!await Database.Authenticate())
                {
                    // Log and exit when unable to authenticate
                    ServerConfiguration.LogError($"Unable to authenticate connection to Cache Server.");
                    return;
                }
                else
                {
                    _lastSuccessfulDbAuth = Utils.GetHighPrecisionUtcTime();

                    // pass to manager
                    await OnDatabaseAuthenticated();

                    // refresh app settings
                    await RefreshAppSettings();

                    #region Check Cache Server Simulated
                    if (Database._settings.SimulatedMode != true)
                    {
                        ServerConfiguration.LogInfo("Connected to Cache Server");
                    }
                    else
                    {
                        ServerConfiguration.LogInfo("Connected to Cache Server (Simulated)");
                    }
                    #endregion
                }
            }

        }

        static async Task StartServerAsync()
        {
            DateTime lastConfigRefresh = Utils.GetHighPrecisionUtcTime();

            string datetime = DateTime.Now.ToString("MMMM/dd/yyyy hh:mm:ss tt");

            ServerConfiguration.LogInfo("**************************************************");
            #region MediusGetBuildTimeStamp
            var MediusBuildTimeStamp = GetLinkerTime(Assembly.GetEntryAssembly());
            ServerConfiguration.LogInfo($"* MediusBuildTimeStamp at {MediusBuildTimeStamp}");
            #endregion

            string gpszVersionString = "3.05.201109161400";

            ServerConfiguration.LogInfo($"* Medius Universe Information Server Version {gpszVersionString}");
            ServerConfiguration.LogInfo($"* Launched on {datetime}");

            if (Database._settings.SimulatedMode == true)
            {
                ServerConfiguration.LogInfo("* Database Disabled Medius Stack");
            }
            else
            {
                ServerConfiguration.LogInfo("* Database Enabled Medius Stack");
            }

            UniverseInfoServers = new MUIS[Settings.Ports.Length];
            for (int i = 0; i < UniverseInfoServers.Length; ++i)
            {
                ServerConfiguration.LogInfo($"* Enabling MUIS on TCP Port = {Settings.Ports[i]}.");
                UniverseInfoServers[i] = new MUIS(Settings.Ports[i]);
                UniverseInfoServers[i].Start();
            }
            /*
            //* Process ID: %d , Parent Process ID: %d
            if (Database._settings.SimulatedMode == true)
            {
                ServerConfiguration.LogInfo("* Database Disabled Medius Universe Information Server");
            } else {
                ServerConfiguration.LogInfo("* Database Enabled Medius Universe Information Server");
            }
            */
            ServerConfiguration.LogInfo($"* Server Key Type: {Settings.EncryptMessages}");

            #region Remote Log Viewing
            if (Settings.RemoteLogViewPort == 0)
            {
                //* Remote log viewing setup failure with port %d.
                ServerConfiguration.LogInfo("* Remote log viewing disabled.");
            }
            else if (Settings.RemoteLogViewPort != 0)
            {
                ServerConfiguration.LogInfo($"* Remote log viewing enabled at port {Settings.RemoteLogViewPort}.");
            }
            #endregion


            #region MediusGetVersion
            if (Settings.MediusServerVersionOverride == true)
            {
                // Use override methods in code to send our own version string from config
                ServerConfiguration.LogInfo("Using config input server version");
                ServerConfiguration.LogInfo($"MUISVersion Version: {Settings.MUISVersion}");

            }
            else
            {
                // Use hardcoded methods in code to handle specific games server versions
                ServerConfiguration.LogInfo("Using game specific server versions");
            }


            #endregion


            //* Diagnostic Profiling Enabled: %d Counts

            ServerConfiguration.LogInfo("**************************************************");

            if (Settings.NATIp != null)
            {
                IPAddress ip = IPAddress.Parse(Settings.NATIp);
                DoGetHostEntry(ip);
            }

            ServerConfiguration.LogInfo($"MUIS initalized.");

            try
            {
                while (true)
                {
                    // Tick
                    await TickAsync();
                    await Task.WhenAll(UniverseInfoServers.Select(x => x.Tick()));

                    // Reload config
                    if ((Utils.GetHighPrecisionUtcTime() - lastConfigRefresh).TotalMilliseconds > Settings.RefreshConfigInterval)
                    {
                        RefreshConfig(false);
                        lastConfigRefresh = Utils.GetHighPrecisionUtcTime();
                    }

                    await Task.Delay(100);
                }
            }
            catch (Exception ex)
            {
                ServerConfiguration.LogError(ex);
            }
            finally
            {
                await Task.WhenAll(UniverseInfoServers.Select(x => x.Stop()));
            }
        }

        public static async Task MuisMain()
        {
            Database = new DbController(DB_CONFIG_FILE);
            Initialize();
            await StartServerAsync();
        }

        static void Initialize()
        {
            RefreshConfig(true);
        }

        /// <summary>
        /// 
        /// </summary>
        static void RefreshConfig(bool firstboot)
        {
            // 
            var serializerSettings = new JsonSerializerSettings()
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
            };

            // Load settings
            if (File.Exists(CONFIG_FILE) && !firstboot)
            {
                // Populate existing object
                JsonConvert.PopulateObject(File.ReadAllText(CONFIG_FILE), Settings, serializerSettings);
            }
            else
            {
                // Add the appids to the ApplicationIds list
                Settings.CompatibleApplicationIds.AddRange(new List<int>
                {
                    21624, 20764, 20371, 22500, 10540, 22920, 21731, 21834, 23624, 20043,
                    20032, 20034, 20454, 20314, 21874, 21244, 20304, 20463, 21614, 20344,
                    20434, 22204, 23360, 21513, 21064, 20804, 20374, 21094, 22274, 20060,
                    10984, 10782, 10421, 10130
                });

                string iptofile = null;

                if (DmeClass.Settings.UsePublicIp || MEDIUS.MediusClass.Settings.UsePublicIp)
                {
                    iptofile = Utils.GetPublicIPAddress();
                }
                else
                {
                    iptofile = Utils.GetLocalIPAddress().ToString();
                }

                // Add default localhost entry
                Settings.Universes.Add(0, new UniverseInfo[] {
                    new UniverseInfo()
                    {
                        Name = "sample universe",
                        Description = null,
                        UserCount = 0,
                        MaxUsers = 0,
                        Endpoint = "url",
                        SvoURL = "url",
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                // Populate with other entries
                Settings.Universes.Add(10130, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Hardware Online Arena Beta",
                        Description = "Beta Universe",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 31,
                        SvoURL = null,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        ExtendedInfo = null,
                        UniverseBilling = "SCEA",
                        BillingSystemName = "Sony Computer Entertainment America, Inc. Billing System"
                    }
                });

                Settings.Universes.Add(10540, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Socom II November Beta",
                        Description = "Beta Universe",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 31,
                        SvoURL = null,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        ExtendedInfo = null,
                        UniverseBilling = "SCEA",
                        BillingSystemName = "Sony Computer Entertainment America, Inc. Billing System"
                    }
                });

                Settings.Universes.Add(10782, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "GT4 Online Public Beta",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 0,
                        MaxUsers = 15000,
                        Endpoint = iptofile,
                        SvoURL = null,
                        ExtendedInfo = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(10421, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Arc the Lad: Generations Preview",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 0,
                        MaxUsers = 10000,
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(10984, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Arc the Lad: EoD US",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 0,
                        MaxUsers = 10000,
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(20060, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "F1 2006",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 0,
                        MaxUsers = 10000,
                        Endpoint = iptofile,
                        SvoURL = $"http://{iptofile}:10060/F12006_SVML/index.jsp ",
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(21064, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Syphon Filter: Logan's Shadow",
                        Description = "Test",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = $"http://{iptofile}:10060/SFO2PSP_SVML/index.jsp ",
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(21094, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Socom Confrontation Prod",
                        Description = "v1.61",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = $"http://{iptofile}:10060/CONFRONTATION_XML/uri/index.jsp ",
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(20804, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Syphon Filter: Logan's Shadow Test Sample",
                        Description = "Test",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = $"http://{iptofile}:10060/SFO2PSP_SVML/index.jsp ",
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(21513, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Syphon Filter: Logan's Shadow Test Sample",
                        Description = "Test",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = $"http://{iptofile}:10060/SFO2PSP_SVML/index.jsp ",
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(22204, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Motorstorm PSP",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = $"http://{iptofile}:10060/MOTORSTORMPSP_SVML/index.jsp ",
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(23360, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Wipeout HD",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = $"http://{iptofile}:10060/wox_ws/rest/main/Start ",
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(20624, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Calling All Cars",
                        Description = null,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = null,
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 2
                    }
                });

                Settings.Universes.Add(20764, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Motorstorm NTSC",
                        Description = "Revival by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 4,
                        SvoURL = null,
                        ExtendedInfo = $"v3.1 http://{iptofile}/frostfight.prod/myuser/BCUS98137",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                    }
                });

                Settings.Universes.Add(20364, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Motorstorm PAL",
                        Description = "Revival by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 5,
                        SvoURL = $"http://{iptofile}:10060/socomcf/index ",
                        ExtendedInfo = $"v3.1 http://{iptofile}/frostfight.prod/myuser/BCES00006",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                    }
                });

                Settings.Universes.Add(21624, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Motorstorm: Pacific Rift",
                        Description = "Revival by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 4,
                        SvoURL = null,
                        ExtendedInfo = null,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                    }
                });

                Settings.Universes.Add(21614, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Wipeout Pulse PSP",
                        Description = "Revival by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 4,
                        SvoURL = "NONE",
                        ExtendedInfo = null,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                    }
                });

                Settings.Universes.Add(20344, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "ATV Offroad Fury Pro PSP",
                        Endpoint = iptofile,
                        Port = 10075,
                        SvoURL = $"http://{iptofile}:10060/ATV4UNIFIED_SVML/index.jsp ",
                    }
                });

                Settings.Universes.Add(20371, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "muis",
                        Description = "01",
                        Endpoint = iptofile,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 15000,
                        SvoURL = $"http://{iptofile}:10060/HUBPS3_SVML/unity/start.jsp ",
                        UniverseBilling = "SCEA",
                        BillingSystemName = "Sony Computer Entertainment America, Inc. Billing System",
                        ExtendedInfo = $"{ServerConfiguration.VersionBetaHDK} http://{iptofile}/dev.{ServerConfiguration.VersionBetaHDK}/",
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                if (ServerConfiguration.VersionRetail == "01.83")
                {
                    Settings.Universes.Add(20374, new UniverseInfo[]
                    {
                        new UniverseInfo()
                        {
                            Name = "CPROD prod1 (HDKONLINEDEBUG MUIS)",
                            Description = "01",
                            Endpoint = iptofile,
                            Status = 1,
                            UserCount = 1,
                            MaxUsers = 15000,
                            SvoURL = $"http://{iptofile}:10060/HUBPS3_SVML/unity/start.jsp ",
                            ExtendedInfo = $"{ServerConfiguration.VersionRetail} http://{iptofile}/{ServerConfiguration.VersionRetail}/",
                            UniverseBilling = "SCEA",
                            BillingSystemName = "Sony Computer Entertainment America, Inc. Billing System",
                            Port = 10075,
                            UniverseId = 1
                        }
                    });
                }
                else
                {
                    Settings.Universes.Add(20374, new UniverseInfo[]
                    {
                        new UniverseInfo()
                        {
                            Name = "CPROD prod1 (Public MUIS)",
                            Description = "01",
                            Endpoint = iptofile,
                            Status = 1,
                            UserCount = 1,
                            MaxUsers = 15000,
                            SvoURL = $"http://{iptofile}:10060/HUBPS3_SVML/unity/start.jsp ",
                            ExtendedInfo = $"{ServerConfiguration.VersionRetail} http://{iptofile}/{ServerConfiguration.VersionRetail}/",
                            UniverseBilling = "SCEA",
                            BillingSystemName = "Sony Computer Entertainment America, Inc. Billing System",
                            Port = 10075,
                            UniverseId = 1
                        }
                    });
                }

                Settings.Universes.Add(21834, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Twisted Metal X Online",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = null,
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(20304, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Socom FTB2",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = $"http://{iptofile}:10060/socom3/index ",
                        ExtendedInfo = $"v1.60 http://{iptofile}/ftb2/manifest.txt",
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(20032, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Socom FTB Pubeta",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = $"http:/{iptofile}:10060/SOCOMPUBETAPSP_SVML/index.jsp ",
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(22920, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Starhawk Dev",
                        Description = "Starhawk Dev LAN Build",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 2,
                        SvoURL = $"http://{iptofile}:10060/BOURBON_XML/uri/URIStore.do ",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        ExtendedInfo = "<XML><A url=`http://blob117.scea.com` latest=`0` access=`0` /></XML>",
                        UniverseBilling = "SCEA",
                        BillingSystemName = "Sony Computer Entertainment America, Inc. Billing System"
                    }
                });

                Settings.Universes.Add(22500, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Motorstorm 3 Apocalypse",
                        Description = "Motorstorm 3 Apocalypse",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 11,
                        SvoURL = $"http://{iptofile}:10060/MOTORSTORM3PS3_XML/ ",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        ExtendedInfo = null,
                    }
                });

                Settings.Universes.Add(21731, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Resistance 2 Private Beta",
                        Description = "Revived by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 2,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                    }
                });

                Settings.Universes.Add(23624, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Buzz! Quiz Player",
                        Description = "Revived by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 2,
                        SvoURL = $"http://{iptofile}:10060/BUZZPS3_XML/index.jsp ",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                    }
                });

                Settings.Universes.Add(20043, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Warhawk Pubeta NTSC",
                        Description = "A Emulated Server project by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 7,
                        SvoURL = "",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 255,
                        ExtendedInfo = $"v1.13 http://{iptofile}/medius-patch/warhawk-pubeta/warhawk/20070608_r012/NPUA80093.cfg"
                    }
                });

                Settings.Universes.Add(20034, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Socom FTB Prod",
                        Description = "Revived by PSORG",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(20454, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Killzone Lib v1.20",
                        Description = "Revival by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 4,
                        SvoURL = $"http://{iptofile}:1006/KILLZONEPSP_SVML/index.jsp ",
                        ExtendedInfo = null,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                    }
                });

                Settings.Universes.Add(20314, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Lemmings PSP",
                        Description = "Revival by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 4,
                        SvoURL = $"http://{iptofile}:10060/LEMMINGSPSP_SVML/index.jsp ",
                        ExtendedInfo = null,
                        UniverseBilling = "SCEA",
                        BillingSystemName = "Sony Computer Entertainment America Inc.",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                    }
                });

                Settings.Universes.Add(21874, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Resistance PSP",
                        Description = "Revival by PSORG",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = $"http://{iptofile}:10060/SOCOMTACTICS_SVML/index.jsp ",
                        ExtendedInfo = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(21244, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Socom Tactical PSP",
                        Description = "Revival by PSORG",
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        Endpoint = iptofile,
                        SvoURL = "NONE",
                        ExtendedInfo = null,
                        UniverseBilling = null,
                        BillingSystemName = null,
                        Port = 10075,
                        UniverseId = 1
                    }
                });

                Settings.Universes.Add(20463, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "Syphon Filter: Dark Mirror Pre-Prod 0.02",
                        Description = "Revived by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        UniverseId = 2,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        SvoURL = "NONE",
                    }
                });

                Settings.Universes.Add(20434, new UniverseInfo[]
                {
                    new UniverseInfo()
                    {
                        Name = "WTS 2006",
                        Description = "Revived by PSORG",
                        Endpoint = iptofile,
                        Port = 10075,
                        Status = 1,
                        UserCount = 1,
                        MaxUsers = 256,
                        SvoURL = $"http://{iptofile}:10060/WTS06_SVML/index.svml ",
                    }
                });

                // Save defaults
                File.WriteAllText(CONFIG_FILE, JsonConvert.SerializeObject(Settings, Formatting.Indented));
            }

            // Update default rsa key
            Server.Pipeline.Attribute.ScertClientAttribute.DefaultRsaAuthKey = Settings.DefaultKey;
        }

        public static void DoGetHostEntry(IPAddress address)
        {
            try
            {
                IPHostEntry host = Dns.GetHostEntry(address);

                ServerConfiguration.LogInfo($"* NAT Service IP: {address}");
                //ServerConfiguration.LogInfo($"GetHostEntry({address}) returns HostName: {host.HostName}");
            }
            catch (SocketException ex)
            {
                //unknown host or
                //not every IP has a name
                //log exception (manage it)
                ServerConfiguration.LogError($"* NAT not resolved {ex}");
            }
        }

        #region System Time
        public static DateTime GetLinkerTime(Assembly assembly)
        {
            const string BuildVersionMetadataPrefix = "+build";

            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attribute?.InformationalVersion != null)
            {
                var value = attribute.InformationalVersion;
                var index = value.IndexOf(BuildVersionMetadataPrefix);
                if (index > 0)
                {
                    value = value[(index + BuildVersionMetadataPrefix.Length)..];
                    return DateTime.ParseExact(value, "yyyy-MM-ddTHH:mm:ss:fffZ", CultureInfo.InvariantCulture);
                }
            }

            return default;
        }

        public static TimeSpan GetUptime()
        {
            ManagementObject mo = new ManagementObject(@"\\.\root\cimv2:Win32_OperatingSystem=@");
            DateTime lastBootUp = ManagementDateTimeConverter.ToDateTime(mo["LastBootUpTime"].ToString());
            return DateTime.Now.ToUniversalTime() - lastBootUp.ToUniversalTime();
        }
        #endregion

        public static async Task OnDatabaseAuthenticated()
        {
            // get supported app ids
            var appids = await Database.GetAppIds();

            // build dictionary of app ids from response
            _appIdGroups = appids.ToDictionary(x => x.Name, x => x.AppIds.ToArray());
        }

        static async Task RefreshAppSettings()
        {
            try
            {
                if (!await Database.AmIAuthenticated())
                    return;

                // get supported app ids
                var appIdGroups = await Database.GetAppIds();
                if (appIdGroups == null)
                    return;

                // get settings
                foreach (var appIdGroup in appIdGroups)
                {
                    foreach (var appId in appIdGroup.AppIds)
                    {
                        var settings = await Database.GetServerSettings(appId);
                        if (settings != null)
                        {
                            if (_appSettings.TryGetValue(appId, out var appSettings))
                            {
                                appSettings.SetSettings(settings);
                            }
                            else
                            {
                                appSettings = new AppSettings(appId);
                                appSettings.SetSettings(settings);
                                _appSettings.Add(appId, appSettings);

                                // we also want to send this back to the server since this is new locally
                                // and there might be new setting fields that aren't yet on the db
                                await Database.SetServerSettings(appId, appSettings.GetSettings());
                            }
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                ServerConfiguration.LogError(ex);
            }
        }

        /// <summary>
        /// Generates a incremental session key number
        /// </summary>
        /// <returns></returns>
        public static string GenerateSessionKey()
        {
            lock (_sessionKeyCounterLock)
            {
                return (++_sessionKeyCounter).ToString();
            }
        }

        public static AppSettings GetAppSettingsOrDefault(int appId)
        {
            if (_appSettings.TryGetValue(appId, out var appSettings))
                return appSettings;

            return _defaultAppSettings;
        }
    }
}
