namespace GenericRepository;

/// <summary>
/// A single page of <typeparamref name="TEntity"/> results, as returned by
/// <see cref="IRepository{TEntity}.GetPagedAsync"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type contained in <see cref="Items"/>.</typeparam>
public class PagedList<TEntity>
{
    /// <summary>The entities on this page.</summary>
    public IEnumerable<TEntity> Items { get; set; } = [];

    /// <summary>Total number of items across all pages, not just this one.</summary>
    public int TotalItemCount { get; set; }

    /// <summary>Number of items requested per page.</summary>
    public int PageSize { get; set; }

    /// <summary>1-based number of this page.</summary>
    public int PageNumber { get; set; }

    /// <summary>Total number of pages, computed from <see cref="TotalItemCount"/> and <see cref="PageSize"/>.</summary>
    public int TotalPages => (int)Math.Ceiling((double)TotalItemCount / PageSize);

    /// <summary><see langword="true"/> if <see cref="PageNumber"/> is not the first page.</summary>
    public bool HasPreviousPage => PageNumber > 1;

    /// <summary><see langword="true"/> if there is at least one page after <see cref="PageNumber"/>.</summary>
    public bool HasNextPage => PageNumber < TotalPages;

    /// <summary>Creates an empty <see cref="PagedList{TEntity}"/> for object-initializer construction.</summary>
    public PagedList() { }

    /// <summary>Creates a <see cref="PagedList{TEntity}"/> from an already-fetched page of items.</summary>
    /// <param name="items">The entities on this page.</param>
    /// <param name="totalItemCount">Total number of items across all pages.</param>
    /// <param name="pageSize">Number of items requested per page.</param>
    /// <param name="pageNumber">1-based number of this page.</param>
    public PagedList(IEnumerable<TEntity> items, int totalItemCount, int pageSize, int pageNumber)
    {
        Items = items; // paged items for the current page
        TotalItemCount = totalItemCount; // total number of items
        PageSize = pageSize;
        PageNumber = pageNumber;
    }
}
