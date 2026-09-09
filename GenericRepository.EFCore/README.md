# GenericRepository.EFCore

A generic repository and Unit of Work implementation for EF Core. It gives you consistent async CRUD, filtering, paging, soft delete, and audit-field tracking across every entity, without writing the same boilerplate repository over and over.

## Install

```
dotnet add package GenericRepository.EFCore
```

## Key Features

- Generic async CRUD (`AddAsync`, `UpdateAsync`, `DeleteAsync`) for any entity — no per-entity repository required.
- `UpdateAsync` is tracking-state aware: attaches detached entities or merges into an already-tracked instance, whichever is correct.
- LINQ querying via `AsQueryable()`, plus `GetAllAsync`/`FindAsync` overloads with predicate and `Include` support. `AsQueryable()` returns a tracked query, matching EF Core's own default.
- Built-in paging via `GetPagedAsync`, returning a `PagedList<T>` with count, page count, and has-next/has-previous. Also available as an `IQueryable<T>` extension, so it works on a query you've already shaped with `.AsNoTracking()` or other custom LINQ, not just on the repository directly.
- Soft delete and restore (`SoftDeleteAsync`/`RestoreAsync`) for `IAuditable` entities — driven entirely by `DeletedAt`, with no separate flag to drift out of sync. Calling either out of state is a safe no-op.
- `GetSoftDeletedAsync` is the only place soft-deleted rows show up; every other query excludes them automatically.
- `GetAllAsync`, `GetPagedAsync`, and `GetSoftDeletedAsync` return tracked results, same as EF Core does by default — no extra behavior to remember when deciding whether you can update what comes back.
- Auditable fields (`CreatedAt`, `UpdatedAt`, `DeletedAt`) are stamped automatically on save.
- Unit of Work pattern via `IUnitOfWork.Of<TEntity>()` — one context, one `SaveChangesAsync()` across entity types.
- Transaction support (`BeginTransactionAsync`) for multi-step operations.
- `DatabaseExistsAsync()` for a quick connectivity check.

## How to Use

### 1. Register it

The package's `UnitOfWork<TDataContext>` already implements `IUnitOfWork` — point it at your `DbContext` and you're done:

```csharp
services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
services.AddScoped<IUnitOfWork, UnitOfWork<AppDbContext>>();
```

If you'd rather inject a plain `IUnitOfWork` without the generic type showing up everywhere, wrap it once:

```csharp
public class UnitOfWork(AppDbContext context) : UnitOfWork<AppDbContext>(context)
{
    public IRepository<User> Users => Of<User>();
}
```

```csharp
services.AddScoped<IUnitOfWork, UnitOfWork>();
```

### 2. Basic CRUD

Everything goes through `Of<TEntity>()`, and nothing is persisted until you call `SaveChangesAsync()` — this is what stamps the auditable fields, so don't skip it in favor of the plain `SaveChange()` method (that one just forwards to EF Core without touching `CreatedAt`/`UpdatedAt`).

```csharp
app.MapGet("/api/products", async (IUnitOfWork uow) =>
    Results.Ok(await uow.Of<Product>().GetAllAsync()));

app.MapGet("/api/products/{id:int}", async (int id, IUnitOfWork uow) =>
{
    var product = await uow.Of<Product>().GetByIdAsync(id);
    return product is null ? Results.NotFound() : Results.Ok(product);
});

app.MapPost("/api/products", async (IUnitOfWork uow, Product product) =>
{
    await uow.Of<Product>().AddAsync(product);
    await uow.SaveChangesAsync();
    return Results.Created($"/api/products/{product.Id}", product);
});

app.MapPut("/api/products/{id:int}", async (int id, Product updated, IUnitOfWork uow) =>
{
    await uow.Of<Product>().UpdateAsync(updated);
    await uow.SaveChangesAsync();
    return Results.NoContent();
});

app.MapDelete("/api/products/{id:int}", async (int id, IUnitOfWork uow) =>
{
    await uow.Of<Product>().DeleteAsync(id);
    await uow.SaveChangesAsync();
    return Results.NoContent();
});
```

### 3. Querying with filters and includes

```csharp
// predicate + eager loading, in one call
var products = await uow.Of<Product>().GetAllAsync(
    p => p.CategoryId == categoryId,
    includes: [p => p.Category]);

// a single match
var product = await uow.Of<Product>().FindAsync(p => p.Sku == sku);

// or drop down to full LINQ when the above isn't enough
var expensiveProducts = uow.Of<Product>().AsQueryable()
    .Include(p => p.Category)
    .Where(p => p.Price > 100)
    .OrderBy(p => p.Name);
```

`GetAllAsync`, `FindAsync`, and `AsQueryable` all exclude soft-deleted rows for entities implementing `IAuditable`, unconditionally — there's no flag to opt out of that per call. If you need to see soft-deleted rows, use `GetSoftDeletedAsync` (below) instead.

#### Read-only browsing with `AsNoTracking`

`AsQueryable()` returns a tracked query, same as EF Core itself. For pure display/browse scenarios where you never call `UpdateAsync` on the results, opt into no-tracking by chaining it yourself; `GetPagedAsync` is also available as an `IQueryable<T>` extension so the fluent chain continues naturally:

```csharp
var page = await uow.Of<Medicine>()
    .AsQueryable()
    .AsNoTracking()
    .GetPagedAsync(
        pageNumber: 1,
        pageSize: 20,
        predicate: m => m.IsActive,
        orderBy: q => q.OrderBy(m => m.Name));
```

This is the same `GetPagedAsync` the repository uses internally — `IRepository<TEntity>.GetPagedAsync(...)` is just `AsQueryable().GetPagedAsync(...)` under the hood — so behavior is identical for a plain, unmodified query. For a plain filtered/eager-loaded list without paging, drop down to `AsQueryable().AsNoTracking()` followed by your own `.Where(...)`/`.Include(...)`/`.ToListAsync()`.

### 4. Paging

```csharp
var page = await uow.Of<Product>().GetPagedAsync(
    pageNumber: 1,
    pageSize: 20,
    predicate: p => p.IsActive,
    orderBy: q => q.OrderBy(p => p.Name));

OR

var page = await uow.Of<Product>().GetPagedAsync(
    pageNumber: 1,
    pageSize: 20,
    predicate: p => p.IsActive,
    orderBy: q => q.OrderBy(p => p.Name),
    includes: r => r.Supplier);


Results.Ok(new { page.Items, page.TotalItemCount, page.TotalPages, page.HasNextPage });
```

### 5. Soft delete and restore

```csharp
await uow.Of<Product>().SoftDeleteAsync(product); // stamps DeletedAt
await uow.SaveChangesAsync();

// or by id, without fetching it first
var wasDeleted = await uow.Of<Product>().SoftDeleteAsync(productId);
await uow.SaveChangesAsync();

await uow.Of<Product>().RestoreAsync(product); // clears DeletedAt
await uow.SaveChangesAsync();

// look at what's in the trash
var deletedProducts = await uow.Of<Product>().GetSoftDeletedAsync();
```

Both methods return a `bool`: `true` if they actually changed something, `false` if the entity wasn't found, doesn't implement `IAuditable`, or was already in the target state (soft-deleting an already-deleted row, or restoring one that isn't deleted).

### 6. Transactions

```csharp
await using var transaction = await uow.BeginTransactionAsync();

try
{
    await uow.Of<Order>().AddAsync(order);
    await uow.Of<Product>().UpdateAsync(product); // e.g. stock decremented earlier
    await uow.SaveChangesAsync();
    await transaction.CommitAsync();
}
catch
{
    await transaction.RollbackAsync();
    throw;
}
```

### 7. Adding entity-specific methods

`Of<TEntity>()` always gives you back the plain generic repository. When an entity needs its own queries, write a small repository for it and register that alongside `IUnitOfWork` for everything else:

```csharp
public interface IProductRepository : IRepository<Product>
{
    Task<IEnumerable<Product>> GetLowStockAsync(int threshold);
}

public class ProductRepository(AppDbContext context)
    : Repository<Product, AppDbContext>(context), IProductRepository
{
    public async Task<IEnumerable<Product>> GetLowStockAsync(int threshold)
        => await AsQueryable().Where(p => p.Stock < threshold).ToListAsync();
}
```

```csharp
services.AddScoped<IProductRepository, ProductRepository>();
```
