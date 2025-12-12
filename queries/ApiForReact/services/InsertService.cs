using System;
using System.Threading.Tasks;
using Supabase;

namespace queries.services;

public class InsertService
{
    private readonly Client _supabase;

    public InsertService(Client supabase)
    {
        _supabase = supabase;
    }


    public async Task<T> InsertAsync<T>(T insert) where T : Supabase.Postgrest.Models.BaseModel, new()
    {
        // Insert the entity into the database
        var response = await _supabase.From<T>().Insert(insert);

        // Check if there are any errors in the response
        if (!response.ResponseMessage.IsSuccessStatusCode)
        {
            throw new Exception($"Error inserting entity: {response.ResponseMessage.ReasonPhrase}");
        }

        return insert;
    }
}