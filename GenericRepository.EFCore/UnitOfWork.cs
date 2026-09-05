using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GenericRepository;

/// <summary>
/// Default <see cref="IUnitOfWork"/> implementation for a given <see cref="DbContext"/> type.
/// Register it directly (<c>services.AddScoped&lt;IUnitOfWork, UnitOfWork&lt;AppDbContext&gt;&gt;()</c>),
/// or derive from it to expose named repository shortcuts alongside <see cref="Of{TEntity}"/>
/// (see the package README for an example).
/// </summary>
/// <typeparam name="TDataContext">The <see cref="DbContext"/> type this unit of work wraps.</typeparam>
public class UnitOfWork<TDataContext>(TDataContext context) : IUnitOfWork where TDataContext : DbContext
{
    private readonly TDataContext _context = context ?? throw new ArgumentNullException(nameof(context));


    /// <inheritdoc/>
    public IRepository<TEntity> Of<TEntity>() where TEntity : class
        => new Repository<TEntity, TDataContext>(_context);

    /// <inheritdoc/>
    public Task<int> SaveChange() => _context.SaveChangesAsync();


    /// <inheritdoc/>
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var timeStamp = DateTime.UtcNow;

        foreach (var entry in _context.ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = timeStamp;
                    entry.Entity.UpdatedAt = timeStamp;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = timeStamp;
                    break;
            }
        }

        return _context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> DatabaseExistsAsync()
    {
        try
        {
            return await _context.Database.CanConnectAsync();
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<IDbContextTransaction> BeginTransactionAsync()
    {
        return await _context.Database.BeginTransactionAsync();
    }

    /// <inheritdoc/>
    public void Dispose() => _context.Dispose();
}
