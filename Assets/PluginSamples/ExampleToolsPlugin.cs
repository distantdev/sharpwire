using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;

namespace SharpwirePlugins.Sample;

/// <summary>
/// Example folder plugin: copy into <c>workspace/plugins/</c> (or keep the auto-seeded copy) and reload plugins.
/// Use only .NET BCL — folder plugins cannot reference Sharpwire.* (host uses a minimal reference set).
/// </summary>
public sealed class ExampleToolsPlugin
{
    [Description("Returns the current UTC time as an ISO 8601 string.")]
    public string GetUtcTimeIso() =>
        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    [Description("Echoes the message back (sample no-op tool).")]
    public string Echo(
        [Description("Text to echo.")] string message) =>
        string.IsNullOrEmpty(message) ? "(empty)" : message;

    [Description("Adds two integers and returns the sum as text.")]
    public string AddIntegers(
        [Description("First operand.")] int a,
        [Description("Second operand.")] int b) =>
        checked(a + b).ToString(CultureInfo.InvariantCulture);

    [Description("Returns after a short delay (demonstrates async tools).")]
    public async Task<string> WaitAndGreet(
        [Description("Name to greet.")] string name)
    {
        await Task.Delay(50).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(name) ? "Hello!" : $"Hello, {name}!";
    }
}
