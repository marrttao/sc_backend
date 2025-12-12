using Supabase.Postgrest.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace queries.services;

public class FetchService
{
    private readonly Supabase.Client _supabase;

    public FetchService(Supabase.Client supabase)
    {
        _supabase = supabase;
    }

    public async Task<List<T>> GetDataAsync<T>(string tableName) where T : BaseModel, new()
    {
        // Fetch data from the specified table
        var response = await _supabase.From<T>().Get();

        // Check if the response is successful
        if (!response.ResponseMessage.IsSuccessStatusCode)
        {
            throw new Exception($"Error fetching data from {tableName}: {response.ResponseMessage.ReasonPhrase}");
        }

        // Return the fetched data
        return response.Models;
    }

    public async Task<List<T>> GetDataByConditionAsync<T>(string tableName, Func<T, bool> condition) where T : BaseModel, new()
    {
        // Fetch data from the specified table
        var response = await _supabase.From<T>().Get();

        // Check if the response is successful
        if (!response.ResponseMessage.IsSuccessStatusCode)
        {
            throw new Exception($"Error fetching data from {tableName}: {response.ResponseMessage.ReasonPhrase}");
        }

        return response.Models.FirstOrDefault(condition) != null
            ? new List<T> { response.Models.FirstOrDefault(condition) }
            : new List<T>();
    }
}