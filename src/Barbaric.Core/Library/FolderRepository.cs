using Dapper;

namespace Barbaric.Core.Library;

/// <summary>The folders the scanner looks for music in.</summary>
public sealed class FolderRepository(LibraryDatabase database)
{
    public async Task<IReadOnlyList<string>> GetAllAsync()
    {
        using var connection = database.Open();
        var folders = await connection.QueryAsync<string>("SELECT path FROM library_folders ORDER BY path COLLATE NOCASE");
        return folders.AsList();
    }

    public async Task AddAsync(string path)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync("INSERT OR IGNORE INTO library_folders (path) VALUES (@path)", new { path = Normalize(path) });
    }

    public async Task RemoveAsync(string path)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync("DELETE FROM library_folders WHERE path = @path", new { path = Normalize(path) });
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
