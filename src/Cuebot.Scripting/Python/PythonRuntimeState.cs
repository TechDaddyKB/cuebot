namespace Cuebot.Scripting;

/// <summary>Availability of the Python interpreter used by "Execute Python code".</summary>
public enum PythonRuntimeState { NotInstalled, Installing, Ready, Error }

/// <summary>A package installed in the Python environment.</summary>
public sealed record PythonPackage(string Name, string Version);
