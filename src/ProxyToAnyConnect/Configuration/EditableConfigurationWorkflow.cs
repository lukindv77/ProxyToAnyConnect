namespace ProxyToAnyConnect.Configuration;

internal readonly record struct EditableConfigurationCommitResult(
    bool IsGloballyValid,
    string? ValidationError)
{
    public static EditableConfigurationCommitResult Valid { get; } = new(true, null);

    public static EditableConfigurationCommitResult Invalid(string error) =>
        new(false, error);
}

internal static class EditableConfigurationWorkflow
{
    public static async Task<EditableConfigurationCommitResult> StageValidateSaveApplyAsync(
        AppOptions desired,
        Action<AppOptions> stageDraft,
        Func<AppOptions, CancellationToken, Task> saveAsync,
        Action<AppOptions> adoptPersisted,
        Func<AppOptions, CancellationToken, Task> applyPersistedAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(stageDraft);
        ArgumentNullException.ThrowIfNull(saveAsync);
        ArgumentNullException.ThrowIfNull(adoptPersisted);
        ArgumentNullException.ThrowIfNull(applyPersistedAsync);

        // A queued GUI command that never actually began before Exit must not publish
        // a new in-memory draft while shutdown is already draining command ownership.
        cancellationToken.ThrowIfCancellationRequested();

        // Staging intentionally precedes whole-configuration validation. The settings
        // editor already validates the object it edited, but an older loaded file may
        // contain another independent invalid object. Keeping this generation in the
        // GUI lets the operator repair those defects sequentially without losing the
        // first correction merely because the complete draft is not valid yet.
        stageDraft(desired);

        try
        {
            desired.Validate();
        }
        catch (InvalidOperationException ex)
        {
            // An invalid draft is expected editable state, not a persistence/runtime
            // failure. Never overwrite the last complete config file and never touch
            // live runtime until the entire staged generation becomes valid.
            return EditableConfigurationCommitResult.Invalid(ex.Message);
        }

        // Preserve the established durable desired-state linearization point. Once
        // save succeeds, runtime failure cannot roll the persisted generation back.
        await saveAsync(desired, cancellationToken);
        adoptPersisted(desired);
        await applyPersistedAsync(desired, cancellationToken);

        return EditableConfigurationCommitResult.Valid;
    }
}
