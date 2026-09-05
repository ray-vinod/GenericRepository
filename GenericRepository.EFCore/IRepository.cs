using System.Linq.Expressions;

namespace GenericRepository;

/// <summary>
/// A generic async repository over <typeparamref name="TEntity"/>, providing the CRUD,
/// querying, paging, and soft-delete operations most EF Core entities need without a
/// hand-written repository per entity type. Obtain an instance via
/// <see cref="IUnitOfWork.Of{TEntity}"/> so changes are tracked against the same
/// <c>DbContext</c> as everything else in the current unit of work.
/// </summary>
/// <typeparam name="TEntity">The entity type this repository manages.</typeparam>
public interface IRepository<TEntity> where TEntity : class
{
    /// <summary>
    /// Finds an entity by its primary key. For entities implementing <see cref="IAuditable"/>,
    /// a soft-deleted row returns <see langword="null"/> here just like everywhere else —
    /// use <see cref="GetSoftDeletedAsync()"/> to look at soft-deleted rows, or
    /// <see cref="RestoreAsync(object[])"/> to bring one back by id directly.
    /// </summary>
    /// <param name="id">The primary key value(s) — more than one for composite keys, in key order.</param>
    /// <returns>The matching, non-soft-deleted entity, or <see langword="null"/> if none exists.</returns>
    Task<TEntity?> GetByIdAsync(params object[] id);

    /// <summary>
    /// Returns every non-soft-deleted entity of this type.
    /// </summary>
    Task<IEnumerable<TEntity>?> GetAllAsync();

    /// <summary>
    /// Returns every non-soft-deleted entity matching <paramref name="predicate"/>, optionally
    /// eager-loading related data.
    /// </summary>
    /// <param name="predicate">Filter applied to the query.</param>
    /// <param name="includes">Navigation properties to eager-load via <c>Include</c>.</param>
    Task<IEnumerable<TEntity>?> GetAllAsync(Expression<Func<TEntity, bool>> predicate, params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// Returns the first non-soft-deleted entity matching <paramref name="predicate"/>,
    /// optionally eager-loading related data.
    /// </summary>
    /// <param name="predicate">Filter applied to the query.</param>
    /// <param name="includes">Navigation properties to eager-load via <c>Include</c>.</param>
    Task<TEntity?> FindAsync(Expression<Func<TEntity, bool>> predicate, params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// Returns every soft-deleted entity of this type. Entities that don't implement
    /// <see cref="IAuditable"/> can never be soft-deleted, so this always returns empty for them.
    /// </summary>
    Task<IEnumerable<TEntity>?> GetSoftDeletedAsync();

    /// <summary>
    /// Returns every soft-deleted entity matching <paramref name="predicate"/>, optionally
    /// eager-loading related data.
    /// </summary>
    /// <param name="predicate">Filter applied to the query.</param>
    /// <param name="includes">Navigation properties to eager-load via <c>Include</c>.</param>
    Task<IEnumerable<TEntity>?> GetSoftDeletedAsync(Expression<Func<TEntity, bool>> predicate, params Expression<Func<TEntity, object>>[] includes);

    /// <summary>
    /// Exposes the underlying <see cref="IQueryable{T}"/> for cases the predicate/include
    /// overloads on this interface don't cover — full LINQ, custom projections, and so on.
    /// Excludes soft-deleted rows for entities implementing <see cref="IAuditable"/>, same as
    /// every other query method here.
    /// </summary>
    IQueryable<TEntity> AsQueryable();

    /// <summary>
    /// Begins tracking <paramref name="entity"/> as a new row. Nothing is persisted until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    Task AddAsync(TEntity entity);

    /// <summary>
    /// Begins tracking <paramref name="entities"/> as new rows. Nothing is persisted until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    Task AddRangeAsync(IEnumerable<TEntity> entities);

    /// <summary>
    /// Updates <paramref name="entity"/>, reconciling whatever EF Core tracking state it
    /// arrives in so the caller doesn't need to know where it came from:
    /// <list type="bullet">
    /// <item>Already tracked (<c>Added</c>/<c>Modified</c>/<c>Unchanged</c>/<c>Deleted</c>): left
    /// untouched — EF's change tracker detects further property changes automatically at
    /// <c>SaveChanges</c> time, so forcing <c>Modified</c> here would corrupt an <c>Added</c>
    /// or <c>Deleted</c> entity's pending operation.</item>
    /// <item>Detached, with a different instance of the same key already tracked locally
    /// (e.g. loaded earlier in this same unit of work): the incoming values are merged into
    /// that tracked instance via <c>CurrentValues.SetValues</c>, instead of attaching a
    /// duplicate, which EF Core would reject with an "already tracked" exception. Only the
    /// properties that actually changed are marked <c>Modified</c>.</item>
    /// <item>Detached and not tracked at all: attached and marked <c>Modified</c> directly,
    /// with no database round-trip.</item>
    /// </list>
    /// For entities implementing <see cref="IAuditable"/>, <c>CreatedAt</c> and <c>DeletedAt</c>
    /// are always excluded from the resulting update regardless of what the incoming
    /// <paramref name="entity"/> carries for them — a disconnected update payload (e.g. a DTO
    /// from a request body) will rarely populate either, and letting them through could violate
    /// the NOT NULL constraint on <c>CreatedAt</c> or silently flip soft-delete state as a side
    /// effect of an unrelated update. Use <see cref="SoftDeleteAsync(TEntity)"/>/
    /// <see cref="RestoreAsync(TEntity)"/> to change <c>DeletedAt</c> intentionally.
    /// Nothing is persisted until <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    Task UpdateAsync(TEntity entity);

    /// <summary>
    /// Finds the entity by primary key and marks it for hard deletion — this removes the row
    /// entirely, regardless of soft-delete state; use <see cref="SoftDeleteAsync(object[])"/>
    /// instead if you want it recoverable. Throws <see cref="ArgumentNullException"/> if no
    /// entity with that key exists — despite the exception type, this signals "not found", not
    /// a null argument. Nothing is persisted until <see cref="IUnitOfWork.SaveChangesAsync"/> is
    /// called.
    /// </summary>
    /// <param name="id">The primary key value(s) — more than one for composite keys, in key order.</param>
    Task DeleteAsync(params object[] id);

    /// <summary>
    /// Marks <paramref name="entity"/> for hard deletion, attaching it first if it isn't
    /// already tracked. This removes the row entirely, regardless of soft-delete state; use
    /// <see cref="SoftDeleteAsync(TEntity)"/> instead if you want it recoverable. Nothing is
    /// persisted until <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    Task DeleteAsync(TEntity entity);

    /// <summary>
    /// For entities implementing <see cref="IAuditable"/>, stamps <c>DeletedAt</c> instead of
    /// removing the row, then routes through <see cref="UpdateAsync"/>. Returns
    /// <see langword="false"/> without making any change if <paramref name="entity"/> doesn't
    /// implement <see cref="IAuditable"/> or is already soft-deleted — calling this twice never
    /// overwrites the original <c>DeletedAt</c>. Nothing is persisted until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    /// <returns><see langword="true"/> if the entity was soft-deleted by this call.</returns>
    Task<bool> SoftDeleteAsync(TEntity entity);

    /// <summary>
    /// Finds the entity by primary key and soft-deletes it. Returns <see langword="false"/>
    /// without making any change if no entity with that key exists, it doesn't implement
    /// <see cref="IAuditable"/>, or it's already soft-deleted. Nothing is persisted until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    /// <param name="id">The primary key value(s) — more than one for composite keys, in key order.</param>
    /// <returns><see langword="true"/> if the entity was soft-deleted by this call.</returns>
    Task<bool> SoftDeleteAsync(params object[] id);

    /// <summary>
    /// For entities implementing <see cref="IAuditable"/>, clears <c>DeletedAt</c>, then routes
    /// through <see cref="UpdateAsync"/>. Returns <see langword="false"/> without making any
    /// change if <paramref name="entity"/> doesn't implement <see cref="IAuditable"/> or isn't
    /// currently soft-deleted. Nothing is persisted until <see cref="IUnitOfWork.SaveChangesAsync"/>
    /// is called.
    /// </summary>
    /// <returns><see langword="true"/> if the entity was restored by this call.</returns>
    Task<bool> RestoreAsync(TEntity entity);

    /// <summary>
    /// Finds the entity by primary key and restores it. Returns <see langword="false"/> without
    /// making any change if no entity with that key exists, it doesn't implement
    /// <see cref="IAuditable"/>, or it isn't currently soft-deleted. Nothing is persisted until
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> is called.
    /// </summary>
    /// <param name="id">The primary key value(s) — more than one for composite keys, in key order.</param>
    /// <returns><see langword="true"/> if the entity was restored by this call.</returns>
    Task<bool> RestoreAsync(params object[] id);

    /// <summary>
    /// Returns one page of non-soft-deleted entities as a <see cref="PagedList{TEntity}"/>,
    /// with item count, page count, and has-next/has-previous already computed.
    /// <paramref name="pageNumber"/> and <paramref name="pageSize"/> are not validated — a
    /// <paramref name="pageSize"/> of zero or a non-positive <paramref name="pageNumber"/> will
    /// produce a meaningless or out-of-range result, so validate them before calling.
    /// </summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="predicate">Optional filter applied before paging.</param>
    /// <param name="orderBy">Optional ordering applied before paging (required for stable results across pages).</param>
    /// <param name="includes">Navigation properties to eager-load via <c>Include</c>.</param>
    Task<PagedList<TEntity>> GetPagedAsync(int pageNumber, int pageSize, Expression<Func<TEntity, bool>>? predicate = null, Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null, params Expression<Func<TEntity, object>>[] includes);
}
