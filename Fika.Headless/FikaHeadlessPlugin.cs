﻿using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using Fika.Core;
using Fika.Core.Coop.GameMode;
using Fika.Core.Coop.Patches;
using Fika.Core.Coop.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.UI.Custom;
using Fika.Core.UI.Patches;
using Fika.Headless.Classes;
using Fika.Headless.Patches;
using Fika.Headless.Patches.Audio;
using Fika.Headless.Patches.DLSS;
using Fika.Headless.Patches.TextureValidateFormat;
using Fika.Headless.Patches.VRAM;
using HarmonyLib;
using Newtonsoft.Json;
using SPT.Common.Http;
using SPT.Custom.Patches;
using SPT.Custom.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Fika.Headless
{
    [BepInPlugin("com.fika.headless", "Fika.Headless", HeadlessVersion)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.SPT.custom", BepInDependency.DependencyFlags.HardDependency)]
    public class FikaHeadlessPlugin : BaseUnityPlugin
    {
        public const string HeadlessVersion = "1.2.3";

        public static FikaHeadlessPlugin Instance { get; private set; }
        public static ManualLogSource FikaHeadlessLogger;
        public static HeadlessRaidController raidController;
        public static bool IsRunningWindows
        {
            get
            {
                return SystemInfo.operatingSystemFamily == OperatingSystemFamily.Windows;
            }
        }

        private static HeadlessWebSocket FikaHeadlessWebSocket;
        private float gcCounter;
        private Coroutine verifyConnectionsRoutine;
        private bool invalidPluginsFound = false;


        public static ConfigEntry<int> UpdateRate { get; private set; }
        public static ConfigEntry<int> RAMCleanInterval { get; private set; }
        public static ConfigEntry<bool> ShouldBotsSleep { get; private set; }
        public static ConfigEntry<bool> ShouldDestroyGraphics { get; private set; }
        public static ConfigEntry<bool> DestroyRenderersOnSceneLoad { get; private set; }

        protected void Awake()
        {
            Instance = this;
            gcCounter = 0;

            FikaHeadlessLogger = Logger;

            SetupConfig();

            new DLSSPatch1().Enable();
            new DLSSPatch2().Enable();
            new DLSSPatch3().Enable();
            new DLSSPatch4().Enable();
            new VRAMPatch1().Enable();
            new VRAMPatch2().Enable();
            new VRAMPatch3().Enable();
            new VRAMPatch4().Enable();
            new SettingsPatch().Enable();
            new SessionResultExitStatusPatch().Enable();
            new MessageWindow_Show_Patch().Enable();
            new HealthTreatmentScreen_IsAvailable_Patch().Enable();
            new CoopPlayer_CreateMovementContext_Patch().Enable();
            new Player_Init_Patch().Enable();
            new ValidateFormatPatch1().Enable();
            new ValidateFormatPatch2().Enable();
            new ValidateFormatPatch3().Enable();
            new GameWorld_OnGameStarted_Patch().Enable();
            new MainMenuControllerClass_method_50_Patch().Enable();
            new ConsoleScreen_OnProfileReceive_Patch().Enable();
            new Class438_Run_Patch().Enable();
            new Player_VisualPass_Patch().Enable();
            new IsReflexAvailablePatch().Enable();
            new AudioSource_Play_Transpiler().Enable();
            new LevelSettings_ApplySettings_Transpiler().Enable();
            new LevelSettings_ApplyTreeWindSettings_Transpiler().Enable();
            new MainMenuControllerClass_method_46_Patch().Enable();
            new MainMenuControllerClass_method_47_Patch().Enable();
            new MainMenuControllerClass_method_74_Patch().Enable();
            new MainMenuControllerClass_method_75_Patch().Enable();
            new LocaleManagerClass_String_0_Patch().Enable();
            new TarkovApplication_method_39_Patch().Enable();
            new ProceduralWeaponAnimation_StartFovCoroutine_Transpiler().Enable();

            if (!ShouldBotsSleep.Value)
            {
                new BotStandBy_Update_Transpiler().Enable();
            }

            if (ShouldDestroyGraphics.Value)
            {
                HeadlessAutoPatcher.EnableDestroyGraphicsPatches();
            }

            HeadlessAutoPatcher.EnableDisableAudioPatches();

            new TarkovApplication_method_18_Patch().Disable();
            new MenuScreen_Awake_Patch().Disable();
            new MemoryCollectionPatch().Disable();
            new SetPreRaidSettingsScreenDefaultsPatch().Disable();

            Logger.LogInfo($"Fika.Headless loaded! OS: {SystemInfo.operatingSystem}");
            if (!IsRunningWindows)
            {
                Logger.LogWarning("You are not running an officially supported operating system by Fika. Minimal support will be given. Please cleanup your '/Logs' folder manually.");
            }
            else
            {
                CleanupLogFiles();
            }

            FikaBackendUtils.IsHeadless = true;

            FikaHeadlessWebSocket = new();

            StartCoroutine(RunPluginValidation());
        }

        private void CleanupLogFiles()
        {
            string exePath = AppContext.BaseDirectory;
            string logsPath = Path.Combine(exePath, "Logs");
            if (!Directory.Exists(logsPath))
            {
                Logger.LogError("CleanupLogFiles: Could not finds '/Logs' folder!");
                return;
            }

            DirectoryInfo logsDir = new(logsPath);
            foreach (DirectoryInfo dir in logsDir.EnumerateDirectories())
            {
                try
                {
                    Logger.LogInfo($"CleanupLogFiles: Deleting {dir.Name}");
                    dir.Delete(true);
                }
                catch
                {
                    Logger.LogWarning($"CleanupLogFiles: Could not delete {dir.Name}, it's probably being used");
                }
            }
        }

        private void SetupConfig()
        {
            UpdateRate = Config.Bind("Headless", "Update Rate", 60,
                new ConfigDescription("How often the server should update (frame cap / tick rate). Only works if your machine can actually reach the selected setting",
                new AcceptableValueRange<int>(30, 120)));

            RAMCleanInterval = Config.Bind("Headless", "RAM Clean Interval", 5,
                new ConfigDescription("How often in minutes the RAM cleaner should run",
                new AcceptableValueRange<int>(1, 30)));

            ShouldBotsSleep = Config.Bind("Headless", "Bot sleeping", false,
                new ConfigDescription("Should the headless host allow bots to sleep? (BSG bot sleeping logic)"));

            ShouldDestroyGraphics = Config.Bind("Headless", "Destroy Graphics", true,
                new ConfigDescription("If the headless plugin should run patches to disable various graphical elements"));

            DestroyRenderersOnSceneLoad = Config.Bind("Headless", "Destroy Renderers", true,
                new ConfigDescription("If the headless plugin should hook scene loading to disable unnecessary renderers as well as unloading all materials (Requires 'Destroy Graphics' to be enabled)"));
        }

        protected void Update()
        {
            gcCounter += Time.unscaledDeltaTime;

            if (gcCounter > (RAMCleanInterval.Value * 60) && FikaGlobals.IsInRaid())
            {
                Logger.LogDebug("Clearing memory");
                gcCounter = 0;
                MemoryControllerClass.Collect(2, GCCollectionMode.Forced, true, true, true);
            }
        }

        public void OnFikaStartRaid(StartHeadlessRequest request)
        {
            if (!TarkovApplication.Exist(out TarkovApplication tarkovApplication))
            {
                throw new NullReferenceException("OnFikaStartRaid: Could not find TarkovApplication!");
            }
            ISession session = tarkovApplication.GetClientBackEndSession();
            if (!session.LocationSettings.locations.TryGetValue(request.LocationId, out LocationSettingsClass.Location location))
            {
                Logger.LogError($"Failed to find location {request.LocationId}");
                return;
            }

            OfflineRaidSettingsMenuPatch_Override.UseCustomWeather = request.CustomWeather;

            Logger.LogInfo($"Starting on location {location.Name}");
            StartCoroutine(BeginFikaStartRaid(request, session, tarkovApplication));
        }

        private IEnumerator RunPluginValidation()
        {
            yield return new WaitForSeconds(5);
            VerifyPlugins();
            while (FikaPlugin.OfficialVersion == null)
            {
                yield return new WaitForSeconds(1);
            }

            FikaPlugin.AutoExtract.Value = true;
            FikaPlugin.QuestTypesToShareAndReceive.Value = 0;
            FikaPlugin.ConnectionTimeout.Value = 30;
            FikaPlugin.UseNamePlates.Value = false;

            FikaPlugin.Instance.AllowFreeCam = true;
            FikaPlugin.Instance.AllowSpectateFreeCam = true;
        }

        private void VerifyPlugins()
        {
            List<string> invalidPluginList =
            [
                "com.Amanda.Graphics",
                "com.Amanda.Sense",
                "VIP.TommySoucy.MoreCheckmarks",
                "com.kmyuhkyuk.EFTApi",
                "com.mpstark.DynamicMaps",
                "IhanaMies.LootValue",
                "com.cactuspie.ramcleanerinterval",
                "com.TYR.DeClutter"
            ];
            PluginInfo[] pluginInfos = [.. Chainloader.PluginInfos.Values];
            List<string> unsupportedMods = [];

            foreach (PluginInfo Info in pluginInfos)
            {
                if (invalidPluginList.Contains(Info.Metadata.GUID))
                {
                    unsupportedMods.Add($"{Info.Metadata.Name}, GUID: {Info.Metadata.GUID}");
                }
            }

            if (unsupportedMods.Count > 0)
            {
                string modsString = string.Join("; ", unsupportedMods);
                Logger.LogFatal($"{unsupportedMods.Count} invalid plugins found, this headless host will not be available for hosting! Remove these mods: {modsString}");
                invalidPluginsFound = true;
                if (IsRunningWindows)
                {
                    MessageBoxHelper.Show($"{unsupportedMods.Count} invalid plugins found, this headless host will not be available for hosting! Check your log files for more information.",
                        "HEADLESS ERROR", MessageBoxHelper.MessageBoxType.OK);
                }
                Thread.Sleep(-1);
                return;
            }

            Logger.LogInfo("Plugins verified successfully");

            if (!invalidPluginsFound)
            {
                FikaHeadlessWebSocket.Connect();
            }
        }

        private IEnumerator VerifyPlayersRoutine()
        {
            yield return new WaitForSeconds(300);
            if (Singleton<FikaServer>.Instance.NetServer.ConnectedPeersCount < 1)
            {
                int attempts = 0;
                while ((CoopGame)Singleton<IFikaGame>.Instance == null && attempts < 5)
                {
                    yield return new WaitForSeconds(5);
                    attempts++;
                    if (attempts >= 5)
                    {
                        Logger.LogError("More than 5 attempts were required to get the CoopGame instance. Something is probably very wrong!");
                    }
                }

                CoopGame coopGame = (CoopGame)Singleton<IFikaGame>.Instance;
                coopGame.StopFromCancel(FikaBackendUtils.Profile.ProfileId, ExitStatus.Runner);
                Logger.LogWarning("The were no connections after 5 minutes, terminating session...");
            }
        }

        private IEnumerator BeginFikaStartRaid(StartHeadlessRequest request, ISession session, TarkovApplication tarkovApplication)
        {
            RaidSettings raidSettings = new RaidSettings
            {
                Side = request.Side,
                PlayersSpawnPlace = request.SpawnPlace,
                MetabolismDisabled = !request.MetabolismDisabled,
                BotSettings = request.BotSettings,
                WavesSettings = request.WavesSettings,
                TimeAndWeatherSettings = request.TimeAndWeatherSettings,
                SelectedLocation = session.LocationSettings.locations.Values.FirstOrDefault(location => location._Id == request.LocationId),
                isInTransition = false,
                RaidMode = ERaidMode.Local,
                IsPveOffline = true
            };

            raidSettings.BotSettings.BotAmount = request.WavesSettings.BotAmount;

            Traverse.Create(tarkovApplication).Field<RaidSettings>("_raidSettings").Value = raidSettings;

            Logger.LogInfo("Initialized raid settings");

            if (FikaPlugin.ForceIP.Value != "")
            {
                // We need to handle DNS entries as well
                string ip = FikaPlugin.ForceIP.Value;
                try
                {
                    IPAddress[] dnsAddress = Dns.GetHostAddresses(FikaPlugin.ForceIP.Value);
                    if (dnsAddress.Length > 0)
                    {
                        ip = dnsAddress[0].ToString();
                    }
                }
                catch
                {

                }

                if (!IPAddress.TryParse(ip, out _))
                {
                    Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen("ERROR FORCING IP",
                        $"'{ip}' is not a valid IP address to connect to! Check your 'Force IP' setting.",
                        ErrorScreen.EButtonType.OkButton, 10f);
                    yield break;
                }
            }

            if (FikaPlugin.ForceBindIP.Value != "Disabled")
            {
                if (!IPAddress.TryParse(FikaPlugin.ForceBindIP.Value, out _))
                {
                    Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen("ERROR BINDING",
                        $"'{FikaPlugin.ForceBindIP.Value}' is not a valid IP address to bind to! Check your 'Force Bind IP' setting.",
                        ErrorScreen.EButtonType.OkButton, 10f);
                    yield break;
                }
            }

            Logger.LogInfo($"Starting with: {JsonConvert.SerializeObject(raidSettings)}");

            Task createMatchTask = FikaBackendUtils.CreateMatch(session.Profile.ProfileId, session.Profile.Info.Nickname, raidSettings);
            while (!createMatchTask.IsCompleted)
            {
                yield return null;
            }

            FikaBackendUtils.IsHeadlessGame = true;

            verifyConnectionsRoutine = StartCoroutine(VerifyPlayersRoutine());

            tarkovApplication.method_37(raidSettings.TimeAndWeatherSettings);
        }
    }
}
