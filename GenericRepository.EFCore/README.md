# GenericRepository.EFCore

A generic repository and Unit of Work implementation for EF Core. It gives you consistent async CRUD, filtering, paging, soft delete, and audit-field tracking across every entity, without writing the same boilerplate repository over and over.

## Install

```
dotnet add package GenericRepository.EFCore
```

## Key Features

- Generic async CRUD (`AddAsync`, `UpdateAsync`, `DeleteAsync`) that works for any entity, no per-entity repository required.
- `UpdateAsync` is tracking-state aware — hand it a detached entity (e.g. straight off a request body) and it figures out whether to attach it fresh or merge it into an entity EF is already tracking, instead of throwing or silently overwriting the wrong thing.
- LINQ-based querying via `AsQueryable()`, plus `GetAllAsync`/`FindAsync` overloads that take a predicate and eager-load `Include`s directly.
- Built-in paging through `GetPagedAsync`, returning a `PagedList<T>` with item count, page count, and has-next/has-previous flags already computed.
- Soft delete and restore (`SoftDeleteAsync`/`RestoreAsync`) for entities that implement `IAuditable` — soft-deleted rows are excluded from every default query automatically. There's no separate "is deleted" flag to keep in sync: a row is soft-deleted exactly when `DeletedAt` is non-null, so the two can never drift apart. Both methods return `false` (and change nothing) if called on an entity that isn't already in the state they'd move it out of — soft-deleting twice never overwrites the original `DeletedAt`, and restoring something that isn't deleted is a no-op.
- `GetSoftDeletedAsync` for looking at the soft-deleted rows themselves — the only place they show up; every other query method excludes them by default with no opt-out flag to misuse.
- Auditable fields (`CreatedAt`, `UpdatedAt`, `DeletedAt`) are stamped automatically on save — you never set them by hand.
- Unit of Work pattern via `IUnitOfWork.Of<TEntity>()`, so one context and one `SaveChangesAsync()` call covers changes across multiple entity types.
- Transaction support (`BeginTransactionAsync`) for multi-step operations that need to succeed or fail together.
- `DatabaseExistsAsync()` for a quick connectivity check before you touch anything.

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
