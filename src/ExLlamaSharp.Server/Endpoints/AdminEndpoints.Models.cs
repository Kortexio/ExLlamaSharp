namespace ExLlamaSharp.Server.Endpoints;

public static partial class AdminEndpoints
{
    private static void MapModelAdminRoutes(RouteGroupBuilder api)
    {
        api.MapGet("/models/library", GetModelLibraryAsync);
        api.MapGet("/models/library/search", SearchLibraryAsync);
        api.MapPost("/models/library", PostModelLibraryAsync);

        api.MapPost("/models/load", LoadModelAsync);
        api.MapGet("/models/{id:guid}/load-profiles", GetLoadProfilesAsync);
        api.MapGet("/models/load-status", GetLoadStatusAsync);
        api.MapPost("/models/load-cancel", CancelLoadAsync);
        api.MapPost("/models/unload", UnloadModelAsync);
        api.MapPost("/models/pull", PullModelAsync);
        api.MapPost("/models/quantize", QuantizeModelAsync);
        api.MapPost("/models/import", ImportModelAsync);
        api.MapPost("/models/alias", AliasModelAsync);
        api.MapPost("/models/rename", RenameModelAsync);
        api.MapDelete("/models/{id:guid}", DeleteModelAsync);

        api.MapGet("/models/{id:guid}/modelfile", GetModelfileAsync);
        api.MapPut("/models/{id:guid}/modelfile", PutModelfileAsync);
        api.MapGet("/models/jobs/{job_id:guid}", GetModelJobAsync);
    }
}