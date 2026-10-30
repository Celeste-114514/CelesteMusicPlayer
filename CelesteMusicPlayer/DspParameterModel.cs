using System.Text.Json;

namespace CelesteMusicPlayer;

/// <summary>Common surface of the DSP parameter models: they own a set of named controls,
/// describe themselves for the web bridge, and accept one explicit change at a time.
/// Presentation projection stays with the host; a model never touches a control.</summary>
internal interface IDspParameterModel
{
    bool Owns(string name);

    object Describe(string name);

    /// <summary>Applies one change; returns false when the request is ignored or equal to the current value.</summary>
    bool Set(string name, JsonElement message);
}
