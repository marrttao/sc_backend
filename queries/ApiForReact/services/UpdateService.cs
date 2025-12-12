using System;
using System.Linq;
using System.Threading.Tasks;
using Supabase;
using Supabase.Postgrest.Models;
using System.Linq.Expressions;

namespace queries.services;

public class UpdateService
{
    private readonly Client _supabase;

    public UpdateService(Client supabase)
    {
        _supabase = supabase;
    }

    public async Task<T> UpdateAsync<T>(T entity) where T : BaseModel, new()
    {
        // Update the entity in the database
        var response = await _supabase.From<T>().Update(entity);

        // Check if there are any errors in the response
        if (!response.ResponseMessage.IsSuccessStatusCode)
        {
            throw new Exception($"Error updating entity: {response.ResponseMessage.ReasonPhrase}");
        }

        return entity;
    }

    public async Task<T> UpdateAsync<T>(string tableName, Expression<Func<T, bool>> condition) where T : BaseModel, new()
    {
        var response = await _supabase.From<T>().Where(condition).Update();
        if (!response.ResponseMessage.IsSuccessStatusCode)
        {
            throw new Exception($"Error updating entity: {response.ResponseMessage.ReasonPhrase}");
        }

        return response.Models.FirstOrDefault();
    }

}
// exxample of update
// var updateService = new UpdateService(supabaseClient);
// var updatedEntity = await updateService.UpdateAsync<YourEntityType>(yourEntityInstance);
// or with condition
// var updatedEntity = await updateService.UpdateAsync<YourEntityType>(tableName, x => x.Id == yourEntityId);
// Make sure to replace YourEntityType and yourEntityInstance with your actual entity type and instance.