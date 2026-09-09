using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace GenericRepository;

/// <summary>
/// A <c>GetPagedAsync</c> overload on <see cref="IQueryable{T}"/> itself, for callers who need
/// to shape the query before paging it — most commonly to opt into <c>AsNoTracking()</c> for a
/// read-only browse, since <see cref="IRepository{TEntity}.AsQueryable"/> returns a tracked
/// query by default, matching EF Core's own default:
/// <code>
/// var page = await _uow.Of&lt;Medicine&gt;()
///     .AsQueryable()
///     .AsNoTracking()
///     .GetPagedAsync(pageNumber, pageSize, orderBy: q => q.OrderBy(m => m.Id));
/// </code>
/// <see cref="IRepository{TEntity}.AsQueryable"/> already excludes soft-deleted rows for
/// <see cref="IAuditable"/> entities, so that filtering carries through automatically no matter
/// what you chain onto it. <see cref="IRepository{TEntity}.GetPagedAsync"/> itself is just
/// <c>AsQueryable().GetPagedAsync(...)</c> under the hood, so this overload always behaves
/// identically for a plain, unmodified query.
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Runs <paramref name="query"/> a page at a time, returning a <see cref="PagedList{TEntity}"/>
    /// with item count, page count, and has-next/has-previous already computed.
    /// <paramref name="pageNumber"/> and <paramref name="pageSize"/> are not validated — a
    /// <paramref name="pageSize"/> of zero or a non-positive <paramref name="pageNumber"/> will
    /// produce a meaningless or out-of-range result, so validate them before calling.
    /// </summary>
    /// <param name="query">
    /// The base query — typically <see cref="IRepository{TEntity}.AsQueryable"/>, optionally
    /// with <c>AsNoTracking()</c> or other shaping already applied.
    /// </param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="predicate">Optional filter applied before paging.</param>
    /// <param name="orderBy">Optional ordering applied before paging (required for stable results across pages).</param>
    /// <param name="includes">Navigation properties to eager-load via <c>Include</c>.</param>
    public static async Task<PagedList<TEntity>> GetPagedAsync<TEntity>(
        this IQueryable<TEntity> query,
        int pageNumber,
        int pageSize,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        params Expression<Func<TEntity, object>>[] includes)
        where TEntity : class
    {
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
}
