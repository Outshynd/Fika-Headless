﻿using EFT.UI;
using HarmonyLib;
using Fika.Core.Patching;
using System.Reflection;
using UnityEngine;

namespace Fika.Headless.Patches.VRAM
{
    // Token: 0x02000009 RID: 9
    public class VRAMPatch1 : FikaPatch
    {
        // Token: 0x0600001A RID: 26 RVA: 0x000023D8 File Offset: 0x000005D8
        protected override MethodBase GetTargetMethod()
        {
            return typeof(EnvironmentUIRoot).GetMethod("SetCameraActive");
        }

        // Token: 0x0600001B RID: 27 RVA: 0x00002400 File Offset: 0x00000600
        [PatchPrefix]
        public static bool Prefix(EnvironmentUIRoot __instance, bool value)
        {
            Transform cameraContainer = Traverse.Create(__instance).Field<Transform>("CameraContainer").Value;
            cameraContainer.gameObject.SetActive(value);
            return false;
        }
    }
}
