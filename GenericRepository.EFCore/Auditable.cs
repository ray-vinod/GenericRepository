namespace GenericRepository;

/// <summary>
/// Convenience base class implementing <see cref="IAuditable"/> — inherit from this instead
/// of implementing the interface by hand.
/// </summary>
public class Auditable : IAuditable
{
    /// <inheritdoc/>
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc/>
    public DateTime UpdatedAt { get; set; }

    /// <inheritdoc/>
    public DateTime? DeletedAt { get; set; }
}
