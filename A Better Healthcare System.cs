using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace ABetterHealthcareSystem
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.zombieghost.abetterhealthcaresystem";
        public const string PluginName = "A Better Healthcare System";
        public const string PluginVersion = "1.0.0";

        private Harmony _harmony;

        private void Awake()
        {
            var enableMovementDuringSurgery = Config.Bind(
                "General",
                "EnableMovementDuringSurgery",
                true,
                "Allow movement while performing surgery.");

            _harmony = new Harmony(PluginGuid);
            SurgeryMovementPatch.Install(_harmony, Logger, enableMovementDuringSurgery);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    internal static class SurgeryMovementPatch
    {
        private static readonly HashSet<object> ActiveOperations = new HashSet<object>();
        private static FieldInfo _finishedField;
        private static BepInEx.Logging.ManualLogSource _logger;
        private static ConfigEntry<bool>? _enableMovementDuringSurgery;

        public static void Install(Harmony harmony, BepInEx.Logging.ManualLogSource logger, ConfigEntry<bool> enableMovementDuringSurgery)
        {
            _logger = logger;
            _enableMovementDuringSurgery = enableMovementDuringSurgery;
            PatchMovementProperty(harmony, "PlayerCharacterController", "IsMoveIgnored", logger);
            PatchMovementProperty(harmony, "EFT.MovementContext", "IsAxesIgnored", logger);
            PatchMovementCapabilities(harmony, logger);
            PatchSurgeryStart(harmony, logger);
        }

        private static void PatchMovementProperty(Harmony harmony, string typeName, string propertyName, BepInEx.Logging.ManualLogSource logger)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                var property = type?.GetProperty(propertyName, AccessTools.all);
                var getter = property?.GetGetMethod(true);
                var setter = property?.GetSetMethod(true);
                if (getter == null || setter == null)
                    return;

                harmony.Patch(getter, postfix: new HarmonyMethod(typeof(SurgeryMovementPatch), nameof(MovementGetterPostfix)));
                harmony.Patch(setter, prefix: new HarmonyMethod(typeof(SurgeryMovementPatch), nameof(MovementSetterPrefix)));
                logger.LogInfo($"Patched {typeName}.{propertyName}.");
            }
            catch (Exception exception)
            {
                logger.LogError($"Failed to patch {typeName}.{propertyName}: {exception}");
            }
        }

        private static void PatchMovementCapabilities(Harmony harmony, BepInEx.Logging.ManualLogSource logger)
        {
            var contextType = AccessTools.TypeByName("EFT.MovementContext");
            if (contextType == null)
                return;

            foreach (var methodName in new[] { "get_CanWalk", "get_CanMoveInProne", "get_CanSprint", "get_CanJump" })
            {
                try
                {
                    var method = AccessTools.Method(contextType, methodName);
                    if (method == null)
                        continue;
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(SurgeryMovementPatch), nameof(CapabilityGetterPostfix)));
                    logger.LogInfo($"Patched EFT.MovementContext.{methodName}.");
                }
                catch (Exception exception)
                {
                    logger.LogError($"Failed to patch EFT.MovementContext.{methodName}: {exception}");
                }
            }
        }

        private static void PatchSurgeryStart(Harmony harmony, BepInEx.Logging.ManualLogSource logger)
        {
            try
            {
                var type = AccessTools.TypeByName("EFT.Player+MedsController+MedsInHandsOperation");
                if (type == null)
                    throw new MissingMethodException("MedsInHandsOperation");

                var start = FindDeclaredMethod(type, "Start") ?? FindDeclaredMethod(type.BaseType, "Start");
                _finishedField = type.GetField("_finished", AccessTools.all);
                if (start == null || _finishedField == null)
                    throw new MissingMethodException("MedsInHandsOperation.Start or _finished");

                harmony.Patch(start, postfix: new HarmonyMethod(typeof(SurgeryMovementPatch), nameof(SurgeryStartedPostfix)));
                logger.LogInfo($"Patched surgical start via {start.DeclaringType?.FullName}.{start.Name}.");
            }
            catch (Exception exception)
            {
                logger.LogError($"Failed to patch surgical start: {exception}");
            }
        }

        private static MethodInfo FindDeclaredMethod(Type type, string methodName)
        {
            if (type == null)
                return null;
            return Array.Find(type.GetMethods(AccessTools.all), method => method.Name == methodName && method.DeclaringType == type);
        }

        private static void SurgeryStartedPostfix(object __instance)
        {
            if (IsSurgicalOperation(__instance))
            {
                ActiveOperations.Add(__instance);
                _logger?.LogInfo("Surgical kit operation started; movement unlocked.");
            }
        }

        private static bool IsSurgicalOperation(object instance)
        {
            return instance != null && instance.GetType().FullName?.Contains("MedsInHandsOperation", StringComparison.Ordinal) == true;
        }

        private static void MovementGetterPostfix(ref bool __result)
        {
            if (IsSurgeryActive())
                __result = false;
        }

        private static void MovementSetterPrefix(ref bool value)
        {
            if (IsSurgeryActive())
                value = false;
        }

        private static void CapabilityGetterPostfix(ref bool __result)
        {
            if (IsSurgeryActive())
                __result = true;
        }

        private static bool IsSurgeryActive()
        {
            if (_enableMovementDuringSurgery?.Value != true)
                return false;

            var active = false;
            var finished = new List<object>();
            foreach (var operation in ActiveOperations)
            {
                if ((bool)_finishedField.GetValue(operation))
                    finished.Add(operation);
                else
                    active = true;
            }

            foreach (var operation in finished)
                ActiveOperations.Remove(operation);
            return active;
        }
    }
}
