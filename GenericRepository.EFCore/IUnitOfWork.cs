using Microsoft.EntityFrameworkCore.Storage;

namespace GenericRepository;

/// <summary>
/// Coordinates one or more <see cref="IRepository{TEntity}"/> instances against a single
/// EF Core <c>DbContext</c>, so changes across multiple entity types are tracked together
/// and persisted with one <see cref="SaveChangesAsync"/> call.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    /// <summary>
    /// Returns a generic repository for <typeparamref name="TEntity"/>, backed by the same
    /// <c>DbContext</c> as every other repository obtained from this unit of work.
    /// </summary>
    IRepository<TEntity> Of<TEntity>() where TEntity : class;

    /// <summary>
    /// Persists tracked changes directly via EF Core, bypassing the audit-field stamping
    /// that <see cref="SaveChangesAsync"/> performs. Entities implementing
    /// <see cref="IAuditable"/> will <b>not</b> get <c>CreatedAt</c>/<c>UpdatedAt</c> stamped
    /// when saved this way. Prefer <see cref="SaveChangesAsync"/> unless you specifically
    /// need to skip that stamping.
    /// </summary>
    Task<int> SaveChange();

    /// <summary>
    /// Stamps <c>CreatedAt</c>/<c>UpdatedAt</c> (and clears <c>IsDeleted</c> on newly added
    /// rows) for every tracked entity implementing <see cref="IAuditable"/>, then persists
    /// all tracked changes. This is the save method most callers want.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a database transaction spanning every repository obtained from this unit of
    /// work, so multiple <see cref="SaveChangesAsync"/>-worthy operations can be committed
    /// or rolled back together.
    /// </summary>
    Task<IDbContextTransaction> BeginTransactionAsync();

    /// <summary>
    /// Checks whether the underlying database can currently be reached, returning
    /// <see langword="false"/> instead of throwing on failure.
    /// </summary>
    Task<bool> DatabaseExistsAsync();
}
