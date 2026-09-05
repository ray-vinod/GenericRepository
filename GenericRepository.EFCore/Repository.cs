using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace GenericRepository;

/// <summary>
/// Default <see cref="IRepository{TEntity}"/> implementation backed by an EF Core
/// <see cref="DbSet{TEntity}"/>. Normally you won't construct this directly — obtain one
/// per entity type via <see cref="IUnitOfWork.Of{TEntity}"/>, or derive from it to add
/// entity-specific query methods (see the package README for an example).
/// </summary>
/// <typeparam name="TEntity">The entity type this repository manages.</typeparam>
/// <typeparam name="TDataContext">The <see cref="DbContext"/> type the entity belongs to.</typeparam>
public class Repository<TEntity, TDataContext>(TDataContext context) : IRepository<TEntity>
    where TEntity : class
    where TDataContext : DbContext
{
    /// <summary>The <see cref="DbContext"/> this repository's changes are tracked against.</summary>
    protected readonly TDataContext _context = context ?? throw new ArgumentNullException(nameof(context));
    internal readonly DbSet<TEntity> _dbSet = context.Set<TEntity>();

    /// <inheritdoc/>
    public async Task AddAsync(TEntity entity) => await _dbSet.AddAsync(entity);

    /// <inheritdoc/>
    public async Task AddRangeAsync(IEnumerable<TEntity> entities) => await _dbSet.AddRangeAsync(entities);

    /// <inheritdoc/>
    public IQueryable<TEntity> AsQueryable()
    {
        var query = _dbSet.AsQueryable();

        if (typeof(IAuditable).IsAssignableFrom(typeof(TEntity)))
        {
            query = query.Where(e => EF.Property<DateTime?>(e, nameof(IAuditable.DeletedAt)) == null);
        }

        return query;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(params object[] id)
    {
        var entity = await _dbSet.FindAsync(id);
        if (entity == null)
        {
            throw new ArgumentNullException(nameof(entity), "Entity not found");
        }

        _dbSet.Remove(entity);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(TEntity entity)
    {
        if (_context.Entry(entity).State == EntityState.Detached)
        {
            _dbSet.Attach(entity);
        }

        _dbSet.Remove(entity);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<TEntity?> FindAsync(
        Expression<Func<TEntity, bool>> predicate,
        params Expression<Func<TEntity, object>>[] includes)
    {
        var query = AsQueryable();
        query = includes.Aggregate(query, (current, include) => current.Include(include));
        return await query.FirstOrDefaultAsync(predicate);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<TEntity>?> GetAllAsync()
    {
        return await AsQueryable().ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<TEntity>?> GetAllAsync(
        Expression<Func<TEntity, bool>> predicate,
        params Expression<Func<TEntity, object>>[] includes)
    {
        var query = AsQueryable();
        query = includes.Aggregate(query, (current, include) => current.Include(include));
        return await query.Where(predicate).ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<TEntity?> GetByIdAsync(params object[] id)
    {
        var entity = await _dbSet.FindAsync(id);
        return entity is IAuditable { DeletedAt: not null } ? null : entity;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<TEntity>?> GetSoftDeletedAsync()
    {
        return await SoftDeletedQueryable().ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<TEntity>?> GetSoftDeletedAsync(
        Expression<Func<TEntity, bool>> predicate,
        params Expression<Func<TEntity, object>>[] includes)
    {
        var query = SoftDeletedQueryable();
        query = includes.Aggregate(query, (current, include) => current.Include(include));
        return await query.Where(predicate).ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<PagedList<TEntity>> GetPagedAsync(
        int pageNumber,
        int pageSize,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        params Expression<Func<TEntity, object>>[] includes)
    {
        var query = AsQueryable();
        query = includes.Aggregate(query, (current, include) => current.Include(include));

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        if (orderBy != null)
        {
            query = orderBy(query);
        }

        var totalItemCount = await query.CountAsync();
        var items = await query.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PagedList<TEntity>
        {
            Items = items,
            TotalItemCount = totalItemCount,
            PageNumber = pageNumber,
            PageSize = pageSize,
        };
    }

    /// <inheritdoc/>
    public async Task<bool> RestoreAsync(TEntity entity)
    {
        if (entity is not IAuditable { DeletedAt: not null } auditable)
        {
            return false;
        }

        auditable.DeletedAt = null;
        await ApplyUpdateAsync(entity, preserveDeletedAt: false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> RestoreAsync(params object[] id)
    {
        var entity = await _dbSet.FindAsync(id);

        if (entity is not IAuditable { DeletedAt: not null } auditable)
        {
            return false;
        }

        auditable.DeletedAt = null;
        await ApplyUpdateAsync(entity, preserveDeletedAt: false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> SoftDeleteAsync(TEntity entity)
    {
        if (entity is not IAuditable { DeletedAt: null } auditable)
        {
            return false;
        }

        auditable.DeletedAt = DateTime.UtcNow;
        await ApplyUpdateAsync(entity, preserveDeletedAt: false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> SoftDeleteAsync(params object[] id)
    {
        var entity = await _dbSet.FindAsync(id);

        if (entity is not IAuditable { DeletedAt: null } auditable)
        {
            return false;
        }

        auditable.DeletedAt = DateTime.UtcNow;
        await ApplyUpdateAsync(entity, preserveDeletedAt: false);
        return true;
    }

    /// <inheritdoc/>
    public Task UpdateAsync(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return ApplyUpdateAsync(entity, preserveDeletedAt: true);
    }

    /// <summary>
    /// Shared implementation behind <see cref="UpdateAsync"/>, <see cref="SoftDeleteAsync(TEntity)"/>,
    /// and <see cref="RestoreAsync(TEntity)"/>. Reconciles whatever EF Core tracking state
    /// <paramref name="entity"/> arrives in (see <see cref="UpdateAsync"/>'s doc comment for the
    /// state-by-state breakdown), then protects audit-managed columns from being clobbered by a
    /// disconnected object that didn't populate them.
    /// </summary>
    /// <param name="preserveDeletedAt">
    /// <see langword="true"/> to keep whatever <c>DeletedAt</c> is currently persisted,
    /// regardless of what <paramref name="entity"/> carries for it (the generic
    /// <see cref="UpdateAsync"/> case); <see langword="false"/> to let the incoming value
    /// through (the <see cref="SoftDeleteAsync(TEntity)"/>/<see cref="RestoreAsync(TEntity)"/>
    /// case, where changing it is the entire point of the call).
    /// </param>
    private Task ApplyUpdateAsync(TEntity entity, bool preserveDeletedAt)
    {
        var entry = _context.Entry(entity);

        if (entry.State != EntityState.Detached)
        {
            return Task.CompletedTask;
        }

        var trackedEntity = FindTrackedLocal(entity);

        if (trackedEntity != null && !ReferenceEquals(trackedEntity, entity))
        {
            var trackedEntry = _context.Entry(trackedEntity);
            trackedEntry.CurrentValues.SetValues(entity);
            PreserveAuditFields(trackedEntry, preserveDeletedAt);
        }
        else
        {
            _dbSet.Attach(entity);
            entry.State = EntityState.Modified;
            PreserveAuditFields(entry, preserveDeletedAt);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Keeps audit-managed columns out of the generated UPDATE for <see cref="IAuditable"/>
    /// entities when the incoming object may not have intentionally set them. <c>CreatedAt</c>
    /// is always protected — nothing legitimately changes it via an update. <c>DeletedAt</c> is
    /// protected only when <paramref name="preserveDeletedAt"/> is <see langword="true"/>, since
    /// <see cref="SoftDeleteAsync(TEntity)"/>/<see cref="RestoreAsync(TEntity)"/> route through
    /// this same path specifically to change it.
    /// </summary>
    private static void PreserveAuditFields(EntityEntry<TEntity> entry, bool preserveDeletedAt)
    {
        if (entry.Entity is not IAuditable)
        {
            return;
        }

        PreserveOriginalValue(entry, nameof(IAuditable.CreatedAt));

        if (preserveDeletedAt)
        {
            PreserveOriginalValue(entry, nameof(IAuditable.DeletedAt));
        }
    }

    /// <summary>
    /// Resets one property back to its original (currently-persisted) value and excludes it
    /// from the generated UPDATE, regardless of what <see cref="EntityEntry.CurrentValues"/>
    /// currently holds for it.
    /// </summary>
    private static void PreserveOriginalValue(EntityEntry<TEntity> entry, string propertyName)
    {
        var property = entry.Property(propertyName);
        property.CurrentValue = property.OriginalValue;
        property.IsModified = false;
    }

    /// <summary>
    /// Finds the entity already tracked in this context's local cache that shares
    /// <paramref name="entity"/>'s primary key, without querying the database.
    /// </summary>
    private TEntity? FindTrackedLocal(TEntity entity)
    {
        var key = _context.Entry(entity).Metadata.FindPrimaryKey();

        if (key == null || key.Properties.Count == 0)
        {
            return null;
        }

        var keyValues = key.Properties
            .Select(p => p.PropertyInfo?.GetValue(entity) ?? p.FieldInfo?.GetValue(entity))
            .ToArray();

        return _dbSet.Local.FirstOrDefault(e =>
            key.Properties
                .Select(p => p.PropertyInfo?.GetValue(e) ?? p.FieldInfo?.GetValue(e))
                .SequenceEqual(keyValues));
    }

    /// <summary>
    /// The base query for soft-deleted rows. Always empty for entities that don't implement
    /// <see cref="IAuditable"/>, since they can never be soft-deleted.
    /// </summary>
    private IQueryable<TEntity> SoftDeletedQueryable()
    {
        if (!typeof(IAuditable).IsAssignableFrom(typeof(TEntity)))
        {
            return Enumerable.Empty<TEntity>().AsQueryable();
        }

        return _dbSet.AsQueryable().Where(e => EF.Property<DateTime?>(e, nameof(IAuditable.DeletedAt)) != null);
    }
}
