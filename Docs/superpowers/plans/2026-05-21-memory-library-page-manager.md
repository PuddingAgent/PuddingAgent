# Memory Library Page Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Notion-style memory library manager inside PuddingPlatformAdmin for browsing, searching, tracing, and guarded editing of MemoryTreeNode, Book, Chapter, SourceReference, and Pointer data.

**Architecture:** Add a PuddingPlatform Admin API facade over `IMemoryLibrary`, then build an Ant Design Pro page at `/memory-library` with a left page tree, center page editor, and right inspector. Runtime memory tools are not used by the UI.

**Tech Stack:** .NET 10, ASP.NET Core controllers, MSTest, EF Core SQLite, React, TypeScript, Umi, Ant Design Pro, `@ant-design/pro-components`.

---

## File Structure

Create:

- `Source/PuddingPlatform/Controllers/Api/MemoryLibraryAdminController.cs`
- `Source/PuddingPlatform/Services/MemoryLibraryAdminService.cs`
- `Source/PuddingPlatformTests/Services/MemoryLibraryAdminServiceTests.cs`
- `Source/PuddingWebApiTests/MemoryLibraryAdminApiControllerTests.cs`
- `Source/PuddingPlatformAdmin/src/pages/memory-library/index.tsx`
- `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageTree.tsx`
- `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageEditor.tsx`
- `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryInspector.tsx`
- `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemorySearchResults.tsx`
- `Source/PuddingPlatformAdmin/src/pages/memory-library/types.ts`
- `Source/PuddingPlatformAdmin/src/pages/memory-library/styles.less`

Modify:

- `Source/PuddingAgent/Program.cs`
- `Source/PuddingPlatformAdmin/config/routes.ts`
- `Source/PuddingPlatformAdmin/src/services/platform/api.ts`
- `Source/PuddingPlatformAdmin/src/locales/zh-CN/menu.ts`
- `Source/PuddingPlatformAdmin/src/locales/en-US/menu.ts`

---

## Task 1: Add Admin DTOs and Read-only Service

**Files:**
- Create: `Source/PuddingPlatform/Services/MemoryLibraryAdminService.cs`
- Test: `Source/PuddingPlatformTests/Services/MemoryLibraryAdminServiceTests.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] **Step 1: Add DTO records**

Create DTO records in `MemoryLibraryAdminService.cs`:

```csharp
namespace PuddingPlatform.Services;

public sealed record MemoryLibraryOverviewDto(
    string WorkspaceId,
    int LibraryCount,
    int BookCount,
    int TreeNodeCount);

public sealed record MemoryLibraryTreeNodeDto(
    string Id,
    string ParentId,
    string Type,
    string Title,
    string? Summary,
    string Status,
    string? BookId,
    IReadOnlyList<MemoryLibraryTreeNodeDto> Children);

public sealed record MemoryBookPageDto(
    string WorkspaceId,
    string LibraryId,
    string BookId,
    string Title,
    string? Summary,
    string Status,
    IReadOnlyList<MemoryChapterSectionDto> Chapters);

public sealed record MemoryChapterSectionDto(
    string ChapterId,
    string BookId,
    string Title,
    string Content,
    string ContentType,
    double Importance,
    long CreatedAt,
    long UpdatedAt);

public sealed record MemorySearchResultDto(
    string BookId,
    string ChapterId,
    string BookTitle,
    string Snippet,
    double Score);
```

- [ ] **Step 2: Add service interface and implementation**

Add:

```csharp
public interface IMemoryLibraryAdminService
{
    Task<MemoryLibraryOverviewDto> GetOverviewAsync(string workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> GetTreeAsync(string workspaceId, string libraryId, CancellationToken ct = default);
    Task<MemoryBookPageDto> GetBookPageAsync(string workspaceId, string bookId, CancellationToken ct = default);
    Task<IReadOnlyList<MemorySearchResultDto>> SearchAsync(string workspaceId, string query, int topK, CancellationToken ct = default);
}
```

Implement using `IMemoryLibrary`:

- `GetOverviewAsync`: list libraries and books scoped by workspace.
- `GetTreeAsync`: load tree children from root and mounted books.
- `GetBookPageAsync`: verify book belongs to workspace by checking its library.
- `SearchAsync`: call `SearchChaptersFtsScopedAsync`.

- [ ] **Step 3: Register DI**

In `Source/PuddingAgent/Program.cs`:

```csharp
builder.Services.AddScoped<IMemoryLibraryAdminService, MemoryLibraryAdminService>();
```

- [ ] **Step 4: Add service tests**

Test:

```csharp
[TestMethod]
public async Task SearchAsync_ShouldOnlyReturnCurrentWorkspace()
{
    await using var scope = await MemoryLibraryTestScope.CreateAsync();
    var service = new MemoryLibraryAdminService(scope.Library);

    var libA = await scope.Library.CreateLibraryAsync("ws-admin-a", "A", null);
    var bookA = await scope.Library.CreateBookAsync(libA.LibraryId, "A", "A");
    await scope.Library.AddChapterAsync(bookA.BookId, "A", "shared needle");

    var libB = await scope.Library.CreateLibraryAsync("ws-admin-b", "B", null);
    var bookB = await scope.Library.CreateBookAsync(libB.LibraryId, "B", "B");
    await scope.Library.AddChapterAsync(bookB.BookId, "B", "shared needle");

    var hits = await service.SearchAsync("ws-admin-a", "shared", 10);

    Assert.IsTrue(hits.All(x => x.BookId == bookA.BookId));
}
```

- [ ] **Step 5: Verify**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --filter MemoryLibraryAdminServiceTests --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingPlatform\Services\MemoryLibraryAdminService.cs Source\PuddingPlatformTests\Services\MemoryLibraryAdminServiceTests.cs Source\PuddingAgent\Program.cs
git commit -m "feat: add memory library admin service"
```

---

## Task 2: Add Admin API Controller

**Files:**
- Create: `Source/PuddingPlatform/Controllers/Api/MemoryLibraryAdminController.cs`
- Test: `Source/PuddingWebApiTests/MemoryLibraryAdminApiControllerTests.cs`

- [ ] **Step 1: Create controller**

Use route:

```csharp
[ApiController]
[Route("api/admin/memory-library")]
public sealed class MemoryLibraryAdminController : ControllerBase
```

Add endpoints:

```csharp
[HttpGet("workspaces/{workspaceId}/overview")]
public Task<MemoryLibraryOverviewDto> GetOverview(string workspaceId, CancellationToken ct)

[HttpGet("workspaces/{workspaceId}/libraries")]
public async Task<IReadOnlyList<LibraryRecord>> GetLibraries(string workspaceId, CancellationToken ct)

[HttpGet("libraries/{libraryId}/tree")]
public Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> GetTree([FromQuery] string workspaceId, string libraryId, CancellationToken ct)

[HttpGet("books/{bookId}")]
public Task<MemoryBookPageDto> GetBook([FromQuery] string workspaceId, string bookId, CancellationToken ct)

[HttpGet("search")]
public Task<IReadOnlyList<MemorySearchResultDto>> Search([FromQuery] string workspaceId, [FromQuery] string query, [FromQuery] int topK = 20, CancellationToken ct = default)
```

- [ ] **Step 2: Add input guards**

Return `400` when:

- `workspaceId` is empty.
- `query` is empty for search.
- `topK` is less than 1 or greater than 100.

- [ ] **Step 3: Add API tests**

Add tests for:

- `GET /api/admin/memory-library/workspaces/default/overview`
- `GET /api/admin/memory-library/search?workspaceId=default&query=test`
- invalid empty query returns 400.

- [ ] **Step 4: Verify**

Run:

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --no-restore --filter MemoryLibraryAdminApiControllerTests --logger "console;verbosity=minimal"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingPlatform\Controllers\Api\MemoryLibraryAdminController.cs Source\PuddingWebApiTests\MemoryLibraryAdminApiControllerTests.cs
git commit -m "feat: expose memory library admin API"
```

---

## Task 3: Add Frontend API Client and Route

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`
- Modify: `Source/PuddingPlatformAdmin/config/routes.ts`
- Modify: `Source/PuddingPlatformAdmin/src/locales/zh-CN/menu.ts`
- Modify: `Source/PuddingPlatformAdmin/src/locales/en-US/menu.ts`
- Create: `Source/PuddingPlatformAdmin/src/pages/memory-library/types.ts`

- [ ] **Step 1: Add frontend types**

Create `types.ts`:

```ts
export interface MemoryLibraryOverviewDto {
  workspaceId: string;
  libraryCount: number;
  bookCount: number;
  treeNodeCount: number;
}

export interface MemoryLibraryTreeNodeDto {
  id: string;
  parentId: string;
  type: 'library' | 'tree_node' | 'book_page';
  title: string;
  summary?: string;
  status: string;
  bookId?: string;
  children: MemoryLibraryTreeNodeDto[];
}

export interface MemoryChapterSectionDto {
  chapterId: string;
  bookId: string;
  title: string;
  content: string;
  contentType: string;
  importance: number;
  createdAt: number;
  updatedAt: number;
}

export interface MemoryBookPageDto {
  workspaceId: string;
  libraryId: string;
  bookId: string;
  title: string;
  summary?: string;
  status: string;
  chapters: MemoryChapterSectionDto[];
}

export interface MemorySearchResultDto {
  bookId: string;
  chapterId: string;
  bookTitle: string;
  snippet: string;
  score: number;
}
```

- [ ] **Step 2: Add API functions**

In `api.ts`:

```ts
export async function getMemoryLibraryOverview(workspaceId: string): Promise<MemoryLibraryOverviewDto> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/overview`);
}

export async function listMemoryLibraries(workspaceId: string): Promise<LibraryRecord[]> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/libraries`);
}

export async function getMemoryLibraryTree(workspaceId: string, libraryId: string): Promise<MemoryLibraryTreeNodeDto[]> {
  return request(`/api/admin/memory-library/libraries/${encodeURIComponent(libraryId)}/tree`, {
    params: { workspaceId },
  });
}

export async function getMemoryBookPage(workspaceId: string, bookId: string): Promise<MemoryBookPageDto> {
  return request(`/api/admin/memory-library/books/${encodeURIComponent(bookId)}`, {
    params: { workspaceId },
  });
}

export async function searchMemoryLibrary(workspaceId: string, query: string, topK = 20): Promise<MemorySearchResultDto[]> {
  return request('/api/admin/memory-library/search', {
    params: { workspaceId, query, topK },
  });
}
```

- [ ] **Step 3: Add route**

In `routes.ts`:

```ts
{
  path: '/memory-library',
  name: 'memoryLibrary',
  icon: 'database',
  component: './memory-library',
}
```

- [ ] **Step 4: Add menu labels**

In zh-CN menu:

```ts
'menu.memoryLibrary': '记忆图书馆',
```

In en-US menu:

```ts
'menu.memoryLibrary': 'Memory Library',
```

- [ ] **Step 5: Verify typecheck**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm typecheck
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingPlatformAdmin\src\services\platform\api.ts Source\PuddingPlatformAdmin\config\routes.ts Source\PuddingPlatformAdmin\src\locales\zh-CN\menu.ts Source\PuddingPlatformAdmin\src\locales\en-US\menu.ts Source\PuddingPlatformAdmin\src\pages\memory-library\types.ts
git commit -m "feat: add memory library admin route and client"
```

---

## Task 4: Build Read-only Page Tree UI

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/memory-library/index.tsx`
- Create: `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageTree.tsx`
- Create: `Source/PuddingPlatformAdmin/src/pages/memory-library/styles.less`

- [ ] **Step 1: Implement page shell**

Use `PageContainer`, `Select`, `Input.Search`, `Button`, `Spin`, `Alert`, `Splitter` or flex layout.

Layout:

```text
toolbar height 48
content height calc(100vh - header)
left width 300
center flex 1
right width 360
```

- [ ] **Step 2: Implement tree component**

`MemoryPageTree` props:

```ts
interface MemoryPageTreeProps {
  loading: boolean;
  data: MemoryLibraryTreeNodeDto[];
  selectedKey?: string;
  onSelect: (node: MemoryLibraryTreeNodeDto) => void;
}
```

Render Ant Design `Tree`.

- [ ] **Step 3: Load workspace and libraries**

On page load:

- call `listWorkspaces()`.
- select `default` if present.
- call `listMemoryLibraries(workspaceId)`.
- select first library.
- call `getMemoryLibraryTree(workspaceId, libraryId)`.

- [ ] **Step 4: Verify manually**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm typecheck
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingPlatformAdmin\src\pages\memory-library
git commit -m "feat: add memory library page tree UI"
```

---

## Task 5: Add Book Page Reader

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageEditor.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/memory-library/index.tsx`

- [ ] **Step 1: Implement editor props**

```ts
interface MemoryPageEditorProps {
  loading: boolean;
  book?: MemoryBookPageDto;
  selectedChapterId?: string;
}
```

- [ ] **Step 2: Render book page**

Render:

- title as `Typography.Title level={3}`.
- summary as secondary text.
- status tag.
- chapters as section blocks with title, content, importance tag.

- [ ] **Step 3: Load book on tree selection**

When selected node has `bookId`, call:

```ts
getMemoryBookPage(workspaceId, node.bookId)
```

- [ ] **Step 4: Add empty states**

Show clear empty states for:

- no workspace.
- no library.
- selected tree node without book.
- book with no chapters.

- [ ] **Step 5: Verify**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm typecheck
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingPlatformAdmin\src\pages\memory-library
git commit -m "feat: render memory book pages"
```

---

## Task 6: Add Search and Inspector Panels

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemorySearchResults.tsx`
- Create: `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryInspector.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/memory-library/index.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`

- [ ] **Step 1: Add source and pointer client functions**

Add:

```ts
export async function listMemorySources(ownerType: string, ownerId: string): Promise<SourceReferenceRecord[]> {
  return request('/api/admin/memory-library/sources', { params: { ownerType, ownerId } });
}

export async function listMemoryPointers(workspaceId: string, sourceType: string, sourceId: string): Promise<PointerRecord[]> {
  return request('/api/admin/memory-library/pointers', { params: { workspaceId, sourceType, sourceId } });
}
```

- [ ] **Step 2: Implement search results**

Render search results as compact list:

- book title.
- snippet.
- score.
- click sets selected book and chapter.

- [ ] **Step 3: Implement inspector tabs**

Tabs:

```text
Info | Sources | Links
```

Info shows ids and metadata.
Sources shows source references.
Links shows pointers.

- [ ] **Step 4: Wire search**

Top search input:

- empty query clears search mode.
- non-empty query calls `searchMemoryLibrary`.
- result click loads book page.

- [ ] **Step 5: Verify**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm typecheck
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add Source\PuddingPlatformAdmin\src\pages\memory-library Source\PuddingPlatformAdmin\src\services\platform\api.ts
git commit -m "feat: add memory search and inspector panels"
```

---

## Task 7: Add Guarded Editing APIs and UI

**Files:**
- Modify: `Source/PuddingPlatform/Controllers/Api/MemoryLibraryAdminController.cs`
- Modify: `Source/PuddingPlatform/Services/MemoryLibraryAdminService.cs`
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`
- Modify: `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageEditor.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageTree.tsx`

- [ ] **Step 1: Add write DTOs**

```csharp
public sealed record CreateMemoryTreeNodeRequest(
    string WorkspaceId,
    string LibraryId,
    string? ParentNodeId,
    string Name,
    string? Summary,
    string NodeType);

public sealed record CreateMemoryBookRequest(
    string WorkspaceId,
    string LibraryId,
    string? NodeId,
    string Title,
    string? Summary);

public sealed record UpdateMemoryBookRequest(string Title, string? Summary);
public sealed record CreateMemoryChapterRequest(string BookId, string Title, string Content, double Importance);
public sealed record UpdateMemoryChapterRequest(string Title, string Content, double Importance);
```

- [ ] **Step 2: Add endpoints**

Add:

```http
POST /api/admin/memory-library/tree-nodes
POST /api/admin/memory-library/books
PUT  /api/admin/memory-library/books/{bookId}
POST /api/admin/memory-library/chapters
PUT  /api/admin/memory-library/chapters/{chapterId}
POST /api/admin/memory-library/books/{bookId}/archive
POST /api/admin/memory-library/chapters/{chapterId}/archive
```

- [ ] **Step 3: Add UI editing controls**

V1 editing controls:

- New page button opens modal.
- New book button opens modal.
- Book title and summary use edit drawer.
- Chapter edit uses drawer with textarea.
- Archive uses `Popconfirm`.

- [ ] **Step 4: Verify**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --filter MemoryLibraryAdminServiceTests --logger "console;verbosity=minimal"
cd Source\PuddingPlatformAdmin
pnpm typecheck
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source\PuddingPlatform Source\PuddingPlatformAdmin\src
git commit -m "feat: add guarded memory page editing"
```

---

## Task 8: Final Verification

**Files:**
- Modify as needed from previous tasks.

- [ ] **Step 1: Backend verification**

Run:

```powershell
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --nologo
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --nologo
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --no-restore --nologo
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

Expected: all pass or only known unrelated warnings.

- [ ] **Step 2: Frontend verification**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm typecheck
pnpm lint
```

Expected: PASS.

- [ ] **Step 3: Browser smoke**

Start the app stack using the repository's existing dev workflow. Open `/memory-library` and verify:

- page loads.
- workspace select works.
- tree loads.
- book page opens.
- search result opens matching book.
- inspector shows source/link tabs.
- mobile width does not overlap controls.

- [ ] **Step 4: Commit final docs if needed**

```powershell
git add Docs\07架构\31ADR-030记忆图书馆Page管理器ADR.md Docs\superpowers\plans\2026-05-21-memory-library-page-manager.md
git commit -m "docs: add memory library page manager plan"
```

---

## Self-Review Checklist

- The UI model is page tree first, not table first.
- Runtime tools are not used as Admin API.
- Every backend endpoint is workspace scoped.
- V1 uses archive instead of physical delete.
- Chapter editing is section-level, not whole-page blob overwrite.
- SourceReferences and Pointers are visible in Inspector.
- Tests cover workspace isolation and by-id ownership guards.
- Frontend layout is usable at desktop, tablet, and mobile widths.

