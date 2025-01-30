﻿using SPT.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches
{
    /// <summary>
    /// This patch skips checking for keys (e.g. Labs)
    /// </summary>
    public class MainMenuControllerClass_method_49_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(MainMenuControllerClass).GetMethod(nameof(MainMenuControllerClass.method_49));
        }

        [PatchPrefix]
        public static bool Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }
    }
}
