namespace ProxyToAnyConnect.Configuration;

/// <summary>
/// Owns the in-memory configuration being repaired/edited by the GUI. A draft may
/// be temporarily invalid so multiple independent legacy/configuration defects can
/// be repaired sequentially. Persistence/runtime publication remains gated on the
/// complete draft validating successfully.
/// </summary>
internal sealed class EditableConfigurationDraft
{
    private AppOptions _current;

    public EditableConfigurationDraft(AppOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
        ValidationError = GetValidationError(initial);
    }

    public AppOptions Current => _current;

    public string? ValidationError { get; private set; }

    public bool IsValid => ValidationError is null;

    /// <summary>
    /// True after an editor has changed the in-memory generation and until that exact
    /// generation crosses the durable save boundary. This remains true for a valid
    /// draft when persistence is cancelled/fails, so the GUI does not falsely report
    /// the unsaved generation as authoritative.
    /// </summary>
    public bool HasUnpersistedChanges { get; private set; }

    public bool Stage(AppOptions candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _current = candidate;
        ValidationError = GetValidationError(candidate);
        HasUnpersistedChanges = true;
        return ValidationError is null;
    }

    public void MarkPersisted(AppOptions persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        var validationError = GetValidationError(persisted);
        if (validationError is not null)
        {
            throw new InvalidOperationException(
                $"Persisted configuration unexpectedly failed validation: {validationError}");
        }

        _current = persisted;
        ValidationError = null;
        HasUnpersistedChanges = false;
    }

    internal static string? GetValidationError(AppOptions candidate)
    {
        try
        {
            candidate.Validate();
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }
}
