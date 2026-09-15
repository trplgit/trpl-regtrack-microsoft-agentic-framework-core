namespace Insights.Domain;

/// <summary>Where an encrypted report landed in blob storage.</summary>
public sealed record BlobLocation(string Container, string Path);
