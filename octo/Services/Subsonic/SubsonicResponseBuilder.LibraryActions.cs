using Microsoft.AspNetCore.Mvc;
using Octo.Models.Settings;
using Octo.Services.Library;

namespace Octo.Services.Subsonic;

public partial class SubsonicResponseBuilder
{
    /// <summary>The OpenSubsonic extension a client checks for before it offers to remove a song.</summary>
    public const string LibraryActionsExtension = "octoLibraryActions";
    public const int LibraryActionsExtensionVersion = 1;

    /// <summary>
    /// The one action version 1 offers. It is the Delete action, named for what a person sees:
    /// the song leaves the library, and the file waits in quarantine.
    /// </summary>
    public const string RemoveAction = "remove";

    /// <summary>
    /// getLibraryActions: what this server lets the caller do. Always JSON. The field names are a
    /// contract with the Octo app.
    /// </summary>
    public IActionResult CreateLibraryActionsResponse(LibraryActionSettings settings, string? username) =>
        CreateJsonResponse(new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["version"] = SubsonicVersion,
            ["type"] = "octo",
            ["openSubsonic"] = true,
            ["libraryActions"] = new Dictionary<string, object?>
            {
                ["enabled"] = settings.Enabled,
                ["allowed"] = settings.IsAllowed(username),
                ["dryRun"] = settings.DryRun,
                ["actions"] = settings.EffectiveActions()
                    .Any(action => action.Action == LibraryAction.Delete && action.Enabled)
                    ? new[] { RemoveAction }
                    : Array.Empty<string>(),
                // 0 means kept until someone removes it by hand.
                ["keepDays"] = settings.EffectiveQuarantineRetentionDays,
            },
        });

    /// <summary>
    /// libraryAction: what happened to one request. Always ok, so the client reads the state
    /// rather than an error, even when nothing was done.
    /// </summary>
    public IActionResult CreateLibraryActionResponse(string songId, LibraryActionOutcome outcome) =>
        CreateJsonResponse(new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["version"] = SubsonicVersion,
            ["type"] = "octo",
            ["openSubsonic"] = true,
            ["libraryAction"] = new Dictionary<string, object?>
            {
                ["id"] = songId,
                ["action"] = RemoveAction,
                ["state"] = outcome.State.ToString().ToLowerInvariant(),
                ["detail"] = outcome.Detail,
            },
        });
}
