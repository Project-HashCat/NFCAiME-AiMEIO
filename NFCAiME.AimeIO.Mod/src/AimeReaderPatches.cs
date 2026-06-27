using System;
using System.Reflection;
using AMDaemon;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Process.Entry;

namespace NFCAiME.AimeIO.Mod
{
    internal static class AimeReaderPatches
    {
        private static bool _loggedInjection;
        private static bool _loggedMissingAimeId;
        private static bool _loggedInvalidAccessCode;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            Patch(harmony, typeof(TryAime), "Execute", nameof(TryAimeExecutePrefix));
            Patch(harmony, typeof(AimeReaderManager), "Execute", nameof(AimeReaderExecutePostfix), true);
            Patch(harmony, typeof(AimeReaderManager), "AnyRead", nameof(AnyReadPrefix));
            Patch(harmony, typeof(AimeReaderManager), "AdvCheck", nameof(AdvCheckPrefix));
            Patch(harmony, typeof(AimeReaderManager), "GetResult", nameof(GetResultPrefix));
            Patch(harmony, typeof(AimeReaderManager), "GetAccessCode", nameof(GetAccessCodePrefix));
            Patch(harmony, typeof(AimeReaderManager), "GetAimeId", nameof(GetAimeIdPrefix));
            Patch(harmony, typeof(AimeReaderManager), "GetOfflineIdString", nameof(GetOfflineIdStringPrefix));
            Patch(harmony, typeof(AimeReaderManager), "GetSegaIdAuthKey", nameof(GetSegaIdAuthKeyPrefix));
        }

        private static bool TryAimeExecutePrefix(TryAime __instance)
        {
            CardPayload payload;
            if (!CardCache.TryGetPayload(out payload))
            {
                return true;
            }

            var accessCodeText = CardCache.GetAccessCode(payload);
            if (!AccessCode.CanMake(accessCodeText))
            {
                LogOnce(ref _loggedInvalidAccessCode, "[NFCAiME] cached card has no valid 20-digit access code");
                return true;
            }

            if (payload.AimeId == 0)
            {
                LogOnce(ref _loggedMissingAimeId, "[NFCAiME] cached card has no aimeId/userId/accountId; skip Unity login injection");
                return true;
            }

            var modeProperty = typeof(TryAime).GetProperty("Mode", BindingFlags.Instance | BindingFlags.NonPublic);
            if (modeProperty != null && string.Equals(
                    Convert.ToString(modeProperty.GetValue(__instance, null)),
                    "Suspend",
                    StringComparison.Ordinal))
            {
                return true;
            }

            var aimeReader = SingletonStateMachine<AmManager, AmManager.EState>.Instance.AimeReader;
            var accessCode = AccessCode.Make(accessCodeText);

            SetField(aimeReader, "currentState", 9);
            SetField(aimeReader, "_result", 3);
            SetField(aimeReader, "_accessCode", accessCode);
            SetField(aimeReader, "_aimeId", new AimeId(payload.AimeId));
            SetField(aimeReader, "_segaIdAuthKey", string.Empty);
            SetField(aimeReader, "_offlineId", AimeOfflineId.Make(accessCode));

            if (modeProperty != null)
            {
                modeProperty.SetValue(__instance, Enum.Parse(modeProperty.PropertyType, "GetResult"), null);
            }

            LogInjectedOnce();
            return true;
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                return;
            }

            if (field.FieldType.IsEnum && value is int)
            {
                field.SetValue(target, Enum.ToObject(field.FieldType, value));
                return;
            }

            field.SetValue(target, value);
        }

        private static void Patch(
            HarmonyLib.Harmony harmony,
            System.Type targetType,
            string targetName,
            string patchName,
            bool postfix = false)
        {
            var target = AccessTools.Method(targetType, targetName);
            var patch = typeof(AimeReaderPatches).GetMethod(patchName, BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || patch == null)
            {
                MelonLogger.Warning("[NFCAiME] patch skipped: " + targetName);
                return;
            }

            harmony.Patch(
                target,
                prefix: postfix ? null : new HarmonyMethod(patch),
                postfix: postfix ? new HarmonyMethod(patch) : null);
        }

        private static void AimeReaderExecutePostfix()
        {
            Singleton<OperationManager>.Instance.IsAliveAimeReader = true;
        }

        private static bool AnyReadPrefix(ref bool __result)
        {
            if (!HasLoginReadyCard())
            {
                return true;
            }

            __result = true;
            LogInjectedOnce();
            return false;
        }

        private static bool AdvCheckPrefix(ref bool __result)
        {
            if (!HasLoginReadyCard())
            {
                return true;
            }

            __result = true;
            return false;
        }

        private static bool GetResultPrefix(ref AimeReaderManager.Result __result)
        {
            if (!HasLoginReadyCard())
            {
                return true;
            }

            __result = AimeReaderManager.Result.Done;
            return false;
        }

        private static bool GetAccessCodePrefix(ref string __result)
        {
            if (!HasLoginReadyCard())
            {
                return true;
            }

            __result = CardCache.GetAccessCode();
            return false;
        }

        private static bool GetAimeIdPrefix(ref AimeId __result)
        {
            CardPayload payload;
            if (!TryGetLoginReadyPayload(out payload))
            {
                return true;
            }

            __result = new AimeId(payload.AimeId);
            return false;
        }

        private static bool GetOfflineIdStringPrefix(ref string __result)
        {
            if (!HasLoginReadyCard())
            {
                return true;
            }

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
            if (!HasLoginReadyCard())
            {
                return true;
            }

            __result = "";
            return false;
        }

        private static bool HasLoginReadyCard()
        {
            CardPayload payload;
            return TryGetLoginReadyPayload(out payload);
        }

        private static bool TryGetLoginReadyPayload(out CardPayload payload)
        {
            return CardCache.TryGetPayload(out payload)
                && payload.AimeId != 0
                && AccessCode.CanMake(CardCache.GetAccessCode(payload));
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

        private static void LogOnce(ref bool flag, string message)
        {
            if (flag)
            {
                return;
            }

            flag = true;
            MelonLogger.Warning(message);
        }
    }
}
