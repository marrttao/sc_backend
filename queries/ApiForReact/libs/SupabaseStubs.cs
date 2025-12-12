#if SUPABASE_STUBS
// Minimal stubs to allow compilation when the Supabase client package isn't referenced.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Supabase
{
    public class SupabaseOptions
    {
        public bool AutoConnectRealtime { get; set; }
    }

    public class Client
    {
        public Client(string url, string key, SupabaseOptions options)
        {
            // no-op
        }

        public Storage.StorageClient Storage { get; } = new Storage.StorageClient();

        public Task InitializeAsync() => Task.CompletedTask;

        public FromQuery<T> From<T>() where T : Postgrest.Models.BaseModel, new() => new();
    }

    public class FromQuery<T> where T : Postgrest.Models.BaseModel, new()
    {
        public Task<PostgrestResponse<T>> Insert(T entity) => Task.FromResult(new PostgrestResponse<T>(entity));
        public Task<PostgrestResponse<T>> Get() => Task.FromResult(new PostgrestResponse<T>());
        public Task<PostgrestResponse<T>> Update(T entity) => Task.FromResult(new PostgrestResponse<T>(entity));
        public FromQuery<T> Where(Expression<Func<T, bool>> predicate) => this;
        public Task<PostgrestResponse<T>> Update() => Task.FromResult(new PostgrestResponse<T>());
        public Task<PostgrestResponse<T>> Delete(T entity) => Task.FromResult(new PostgrestResponse<T>(entity));
        public Task<PostgrestResponse<T>> Delete() => Task.FromResult(new PostgrestResponse<T>());
    }

    public class PostgrestResponse<T>
    {
        public PostgrestResponse() { }
        public PostgrestResponse(T entity) => Models = new List<T> { entity };

        public HttpResponseMessage ResponseMessage { get; set; } = new(HttpStatusCode.OK);
        public List<T> Models { get; set; } = new();
    }
}

namespace Supabase.Postgrest.Models
{
    public class BaseModel
    {
    }
}

namespace Supabase.Storage
{
    public class StorageClient
    {
        public StorageFileApi From(string bucket) => new(bucket);
    }

    public class StorageFileApi
    {
        private readonly string _bucket;

        public StorageFileApi(string bucket)
        {
            _bucket = bucket;
        }

        public Task Upload(Stream stream, string path, FileOptions? options = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string GetPublicUrl(string path) => $"https://example.com/storage/{_bucket}/{path}";
    }

    public class FileOptions
    {
        public bool Upsert { get; set; }
        public string? CacheControl { get; set; }
        public string? ContentType { get; set; }
    }
}
#endif
