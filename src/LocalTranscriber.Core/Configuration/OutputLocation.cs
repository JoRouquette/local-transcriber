namespace LocalTranscriber.Core.Configuration;

/// <summary>
/// Racine de sortie resolue, partagee via DI. Sert de base au garde-fou de chemin
/// (toute lecture cote MCP doit rester sous cette racine) et aux ressources MCP.
/// </summary>
public sealed record OutputLocation(string OutputRoot);
