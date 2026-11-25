using Bloxstrap.Enums.FlagPresets;

namespace Bloxstrap
{
    public class FastFlagManager : JsonManager<Dictionary<string, object>>
    {
        private static readonly List<string> _validPrefixes = new()
        {
            "FFlag", "DFFlag", "SFFlag", "FInt", "DFInt", "FString", "DFString", "FLog", "DFLog"
        };

        // values must match the entire string to avoid cases where half the string
        // matches but the filter would still be invalid
        private static readonly Regex _boolFilterPattern = new("^(?:true|false)(;[\\d]{1,})+$", RegexOptions.IgnoreCase);
        private static readonly Regex _intFilterPattern = new("^([\\d]{1,})?(;[\\d]{1,})+$", RegexOptions.IgnoreCase);
        private static readonly Regex _stringFilterPattern = new("^[^;]*(;[\\d]{1,})+$", RegexOptions.IgnoreCase);

        internal static string? GetValidationError(string name, string value)
        {
            if (!_validPrefixes.Any(prefix => name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)))
                return Strings.Menu_FastFlagEditor_InvalidPrefix;
            if (!name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
                return Strings.Menu_FastFlagEditor_InvalidCharacter;
            if (name.EndsWith("_PlaceFilter", StringComparison.Ordinal) || name.EndsWith("_DataCenterFilter", StringComparison.Ordinal))
                return ValidateFilter(name, value) ? null : Strings.Menu_FastFlagEditor_InvalidPlaceFilter;
            if ((name.StartsWith("FInt") || name.StartsWith("DFInt")) &&
                !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return Strings.Menu_FastFlagEditor_InvalidNumberValue;
            if ((name.StartsWith("FFlag") || name.StartsWith("DFFlag") || name.StartsWith("SFFlag")) &&
                !bool.TryParse(value, out _))
                return Strings.Menu_FastFlagEditor_InvalidBoolValue;
            return null;
        }

        private static bool ValidateFilter(string name, string value)
        {
            if(name.StartsWith("FFlag") || name.StartsWith("DFFlag"))
                return _boolFilterPattern.IsMatch(value);
            if (name.StartsWith("FInt") || name.StartsWith("DFInt"))
                return _intFilterPattern.IsMatch(value);
            if (name.StartsWith("FString") || name.StartsWith("DFString") || name.StartsWith("FLog") || name.StartsWith("DFLog"))
                return _stringFilterPattern.IsMatch(value);

            return false;
        }

        private readonly bool _studio;

        public FastFlagManager(bool studio = false) => _studio = studio;

        public override string ClassName => nameof(FastFlagManager);

        public override string LOG_IDENT_CLASS => ClassName;

        public override string FileName => "ClientAppSettings.json";

        public override string FileLocation => _studio
            ? Path.Combine(Paths.Base, "StudioClientAppSettings.json")
            : Path.Combine(Paths.Modifications, "ClientSettings", FileName);

        public bool Changed => HasUnsavedChanges;

        // Published Roblox Player allowlist, checked November 14, 2026.
        // https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569/
        public static IReadOnlySet<string> PlayerAllowlist { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            "DFIntCSGLevelOfDetailSwitchingDistance",
            "DFIntCSGLevelOfDetailSwitchingDistanceL12",
            "DFIntCSGLevelOfDetailSwitchingDistanceL23",
            "DFIntCSGLevelOfDetailSwitchingDistanceL34",
            "FFlagHandleAltEnterFullscreenManually",
            "DFFlagTextureQualityOverrideEnabled",
            "DFIntTextureQualityOverride",
            "FIntDebugForceMSAASamples",
            "DFFlagDisableDPIScale",
            "FFlagDebugGraphicsPreferD3D11",
            "FFlagDebugSkyGray",
            "DFFlagDebugPauseVoxelizer",
            "DFIntDebugFRMQualityLevelOverride",
            "FIntFRMMaxGrassDistance",
            "FIntFRMMinGrassDistance",
            "FFlagDebugGraphicsPreferVulkan",
            "FFlagDebugGraphicsPreferOpenGL",
            "FIntGrassMovementReducedMotionFactor",
            "DFFlagSkipHighResolutiontextureMipsOnLowMemoryDevices"
        };

        public static IReadOnlyDictionary<string, string> PresetFlags = new Dictionary<string, string>
        {
            { "Rendering.ManualFullscreen", "FFlagHandleAltEnterFullscreenManually" },
            { "Rendering.DisableScaling", "DFFlagDisableDPIScale" },
            { "Rendering.MSAA", "FIntDebugForceMSAASamples" },

            { "Rendering.TextureQuality.OverrideEnabled", "DFFlagTextureQualityOverrideEnabled" },
            { "Rendering.TextureQuality.Level", "DFIntTextureQualityOverride" },
        };

        public static IReadOnlyDictionary<MSAAMode, string?> MSAAModes => new Dictionary<MSAAMode, string?>
        {
            { MSAAMode.Default, null },
            { MSAAMode.x1, "1" },
            { MSAAMode.x2, "2" },
            { MSAAMode.x4, "4" }
        };

        public static IReadOnlyDictionary<TextureQuality, string?> TextureQualityLevels => new Dictionary<TextureQuality, string?>
        {
            { TextureQuality.Default, null },
            { TextureQuality.Level0, "0" },
            { TextureQuality.Level1, "1" },
            { TextureQuality.Level2, "2" },
            { TextureQuality.Level3, "3" },
        };

        // all fflags are stored as strings
        // to delete a flag, set the value as null
        public void SetValue(string key, object? value)
        {
            const string LOG_IDENT = "FastFlagManager::SetValue";

            if (value is null)
            {
                if (Prop.ContainsKey(key))
                    App.Logger.WriteLine(LOG_IDENT, $"Deletion of '{key}' is pending");

                Prop.Remove(key);
            }
            else
            {
                if (Prop.ContainsKey(key))
                {
                    if (value.ToString() == Prop[key]?.ToString())
                        return;

                    App.Logger.WriteLine(LOG_IDENT, $"Changing of '{key}' from '{Prop[key]}' to '{value}' is pending");
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Setting of '{key}' to '{value}' is pending");
                }

                Prop[key] = value.ToString()!;
            }
        }

        // this returns null if the fflag doesn't exist
        public string? GetValue(string key)
        {
            // check if we have an updated change for it pushed first
            if (Prop.TryGetValue(key, out object? value) && value is not null)
                return value.ToString();

            return null;
        }

        public void SetPreset(string prefix, object? value)
        {
            foreach (var pair in PresetFlags.Where(x => x.Key.StartsWith(prefix)))
                SetValue(pair.Value, value);
        }

        public void SetPresetEnum(string prefix, string target, object? value)
        {
            foreach (var pair in PresetFlags.Where(x => x.Key.StartsWith(prefix)))
            {
                if (pair.Key.StartsWith($"{prefix}.{target}"))
                    SetValue(pair.Value, value);
                else
                    SetValue(pair.Value, null);
            }
        }

        public string? GetPreset(string name)
        {
            if (!PresetFlags.ContainsKey(name))
            {
                App.Logger.WriteLine("FastFlagManager::GetPreset", $"Could not find preset {name}");
                Debug.Assert(false, $"Could not find preset {name}");
                return null;
            }

            return GetValue(PresetFlags[name]);
        }

        public T GetPresetEnum<T>(IReadOnlyDictionary<T, string> mapping, string prefix, string value) where T : Enum
        {
            foreach (var pair in mapping)
            {
                if (pair.Value == "None")
                    continue;

                if (GetPreset($"{prefix}.{pair.Value}") == value)
                    return pair.Key;
            }

            return mapping.First().Key;
        }

        public override void Save()
        {
            // convert all flag values to strings before saving

            foreach (var pair in Prop)
                Prop[pair.Key] = pair.Value?.ToString() ?? "";

            base.Save();

        }

        public override bool Load(bool alertFailure = true)
        {
            bool result = base.Load(alertFailure);

            if (!_studio && GetPreset("Rendering.ManualFullscreen") is null)
                SetPreset("Rendering.ManualFullscreen", "False");

            return result;
        }
    }
}
