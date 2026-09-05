namespace GenericRepository;

/// <summary>
/// Opt-in contract for entities that want automatic audit-field stamping and soft delete.
/// Implement this (or inherit <see cref="Auditable"/>) and <see cref="IUnitOfWork.SaveChangesAsync"/>
/// stamps <see cref="CreatedAt"/>/<see cref="UpdatedAt"/> for you, while
/// <see cref="IRepository{TEntity}.SoftDeleteAsync(TEntity)"/>/<see cref="IRepository{TEntity}.RestoreAsync(TEntity)"/>
/// manage <see cref="DeletedAt"/>, and query methods like <see cref="IRepository{TEntity}.GetAllAsync()"/>
/// filter soft-deleted rows out by default. There is no separate "is deleted" flag — a row is
/// soft-deleted exactly when <see cref="DeletedAt"/> is non-null, so the two can never drift
/// out of sync with each other.
/// </summary>
public interface IAuditable
{
    /// <summary>
    /// When this entity was first saved. Stamped automatically by
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> on insert and never changed afterward.
    /// </summary>
    DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this entity was last saved — at creation, at every update, and at soft-delete or
    /// restore. Stamped automatically by <see cref="IUnitOfWork.SaveChangesAsync"/>.
    /// </summary>
    DateTime UpdatedAt { get; set; }

    /// <summary>
    /// When this entity was soft-deleted, or <see langword="null"/> if it's active. This is the
    /// single source of truth for soft-delete state: rows with a non-null value are excluded
    /// from every default query, reachable only via
    /// <see cref="IRepository{TEntity}.GetSoftDeletedAsync()"/>, and brought back via
    /// <see cref="IRepository{TEntity}.RestoreAsync(TEntity)"/>.
    /// </summary>
    DateTime? DeletedAt { get; set; }
}
