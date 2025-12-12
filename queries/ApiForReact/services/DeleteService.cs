using Supabase.Postgrest.Models;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace queries.services;

public class DeleteService
{
    private readonly Supabase.Client _supabase;

    public DeleteService(Supabase.Client supabase)
    {
        _supabase = supabase;
    }

    // Delete a specific entity
    public async Task<T> DeleteAsync<T>(T entity) where T : BaseModel, new()
    {
        var response = await _supabase.From<T>().Delete(entity);
        if (!response.ResponseMessage.IsSuccessStatusCode)
        {
            throw new Exception($"Error deleting entity: {response.ResponseMessage.ReasonPhrase}");
        }

        return response.Models.Count > 0 ? response.Models[0] : entity;
    }

    // Delete by condition, e.g. .From<City>().Where(x => x.Id == 342).Delete();
    public async Task<List<T>> DeleteByConditionAsync<T>(Expression<Func<T, bool>> condition) where T : BaseModel, new()
    {
        await _supabase.From<T>().Where(condition).Delete();
        return new List<T>();
    }
}
