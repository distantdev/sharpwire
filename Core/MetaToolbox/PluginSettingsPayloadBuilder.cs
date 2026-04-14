using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Sharpwire.Core.MetaToolbox;

public static class PluginSettingsPayloadBuilder
{
    /// <summary>Writes <paramref name="payload"/> onto <paramref name="instance"/> for each property in <paramref name="definition"/> (by property name).</summary>
    public static void ApplyPayloadToInstance(object instance, PluginSettingInfo definition, IReadOnlyDictionary<string, object> payload)
    {
        var t = instance.GetType();
        foreach (var prop in definition.Properties)
        {
            if (!payload.TryGetValue(prop.PropertyName, out var value))
                continue;

            var pi = t.GetProperty(prop.PropertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi?.CanWrite != true)
                continue;

            try
            {
                if (value == null)
                {
                    if (pi.PropertyType == typeof(string))
                        pi.SetValue(instance, string.Empty);
                    continue;
                }

                var targetType = pi.PropertyType;
                var ut = Nullable.GetUnderlyingType(targetType) ?? targetType;
                if (targetType.IsInstanceOfType(value))
                {
                    pi.SetValue(instance, value);
                    continue;
                }

                if (ut.IsEnum)
                {
                    if (value is string s && Enum.TryParse(ut, s, ignoreCase: true, out var ev))
                        pi.SetValue(instance, ev);
                    continue;
                }

                pi.SetValue(instance, Convert.ChangeType(value, ut));
            }
            catch
            {
                // Skip properties that cannot be coerced; plugin still gets OnSettingsLoaded.
            }
        }
    }

    public static Dictionary<string, object> Build(PluginSettingInfo definition, IReadOnlyDictionary<string, string>? stored)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in definition.Properties)
        {
            string? raw = null;
            if (stored != null && stored.TryGetValue(prop.PropertyName, out var found))
                raw = found;
            d[prop.PropertyName] = Coerce(raw, prop.PropertyType);
        }

        return d;
    }

    private static object Coerce(string? s, Type t)
    {
        var ut = Nullable.GetUnderlyingType(t) ?? t;

        if (ut == typeof(string))
            return s ?? string.Empty;

        if (ut == typeof(bool))
            return bool.TryParse(s, out var b) && b;

        if (ut == typeof(int))
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

        if (ut == typeof(long))
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0L;

        if (ut == typeof(float))
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;

        if (ut == typeof(double))
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;

        if (ut == typeof(decimal))
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : 0m;

        if (ut.IsEnum && s != null && Enum.TryParse(ut, s, ignoreCase: true, out var enumVal) && enumVal != null)
            return enumVal;

        return s ?? string.Empty;
    }
}
