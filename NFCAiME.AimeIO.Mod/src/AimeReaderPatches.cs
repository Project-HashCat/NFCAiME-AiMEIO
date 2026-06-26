using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace NFCAiME.AimeIO.Mod
{
    internal static class AimeReaderPatches
    {
        private static bool _loggedInjection;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            var type = AccessTools.TypeByName("Manager.AimeReaderManager");
            if (type == null)
            {
                MelonLogger.Warning("[NFCAiME] Manager.AimeReaderManager not found");
                return;
            }

            Patch(harmony, type, "AnyRead", nameof(AnyReadPrefix));
            Patch(harmony, type, "AdvCheck", nameof(AdvCheckPrefix));
            Patch(harmony, type, "GetResult", nameof(GetResultPrefix));
            Patch(harmony, type, "GetAccessCode", nameof(GetAccessCodePrefix));
            Patch(harmony, type, "GetOfflineIdString", nameof(GetOfflineIdStringPrefix));
            Patch(harmony, type, "GetSegaIdAuthKey", nameof(GetSegaIdAuthKeyPrefix));
        }

        private static void Patch(HarmonyLib.Harmony harmony, Type targetType, string targetName, string prefixName)
        {
            var target = AccessTools.Method(targetType, targetName);
            var prefix = typeof(AimeReaderPatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || prefix == null)
            {
                MelonLogger.Warning("[NFCAiME] patch skipped: " + targetName);
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        private static bool AnyReadPrefix(ref bool __result)
        {
            if (!CardCache.HasValidCard())
            {
                return true;
            }

            __result = true;
            LogInjectedOnce();
            return false;
        }

        private static bool AdvCheckPrefix(ref bool __result)
        {
            if (!CardCache.HasValidCard())
            {
                return true;
            }

            __result = true;
            return false;
        }

        private static bool GetResultPrefix(MethodBase __originalMethod, ref object __result)
        {
            if (!CardCache.HasValidCard())
            {
                return true;
            }

            var method = __originalMethod as MethodInfo;
            if (method == null)
            {
                return true;
            }

            var resultType = method.ReturnType;
            __result = Enum.ToObject(resultType, 3);
            return false;
        }

        private static bool GetAccessCodePrefix(ref string __result)
        {
            var code = CardCache.GetAccessCode();
            if (string.IsNullOrWhiteSpace(code))
            {
                return true;
            }

            __result = code;
            return false;
        }

        private static bool GetOfflineIdStringPrefix(ref string __result)
        {
            var offlineId = CardCache.GetOfflineId();
            if (string.IsNullOrWhiteSpace(offlineId))
            {
                return true;
            }

            __result = offlineId;
            return false;
        }

        private static bool GetSegaIdAuthKeyPrefix(ref string __result)
        {
            if (!CardCache.HasValidCard())
            {
                return true;
            }

            __result = "";
            return false;
        }

        private static void LogInjectedOnce()
        {
            if (_loggedInjection)
            {
                return;
            }

            _loggedInjection = true;
            MelonLogger.Msg("[NFCAiME] injected cached card into AimeReaderManager");
        }
    }
}
