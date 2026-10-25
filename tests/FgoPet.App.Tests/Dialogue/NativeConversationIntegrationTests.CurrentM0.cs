using System.IO;
using System.Net.Http;
using System.Text.Json;
using FgoPet.App.Bootstrap;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

[Collection("H1 full catalog isolation")]
public sealed class CurrentM0FullCatalogTests
{
    [Fact] public async Task Official_route_complete_catalog_is_remeasured_with_original_output_and_task_input_guard()
    {
        var previous=Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT");
        var root=Path.Combine(Path.GetTempPath(),"fgo-current-m0-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT",root);
        try
        {
            var database=new RuntimeDatabase(Path.Combine(root,"runtime.db"),pooling:false);new RuntimeDatabaseMigrator(database).Migrate();
            var services=new ServiceCollection().AddFgoPet([]);services.Replace(ServiceDescriptor.Singleton(database));
            var credentials=new DeniedCredentials();
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Core.Secrets.ICredentialReader>(credentials));
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Infrastructure.Secrets.ICredentialReader>(credentials));
            using var handler=new DeniedHttp();using var http=new HttpClient(handler);services.Replace(ServiceDescriptor.Singleton(http));
            await using var provider=services.BuildServiceProvider();
            var tools=provider.GetRequiredService<PluginCatalog>().Tools.Select(t=>(IToolProvider)new DescriptorOnly(t.Descriptor)).ToArray();
            foreach(var native in new[]{false,true})
            {
                var metadata=await NativeConversationIntegrationTests.MeasureCurrentOfficialM0Async(tools,native);
                var text=JsonSerializer.Serialize(metadata,new JsonSerializerOptions{WriteIndented=true});
                var output=Environment.GetEnvironmentVariable("FGO_PET_M0_CURRENT_EVIDENCE_ROOT");
                if(!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);var file=Path.Combine(output,native?"native.json":"ordinary.json");
                    Assert.False(File.Exists(file));await File.WriteAllTextAsync(file,text);
                }
                Console.WriteLine("CURRENT_M0_METADATA_ONLY "+JsonSerializer.Serialize(metadata));
            }
            var supported = await NativeConversationIntegrationTests.MeasureSupportedOfficialP5Async(tools);
            Console.WriteLine("P5_SUPPORTED_CATALOG_METADATA_ONLY " + JsonSerializer.Serialize(supported));
            Assert.Equal(0,credentials.Reads);Assert.Equal(0,handler.Sends);
        }
        finally{Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT",previous);}
    }
    private sealed class DescriptorOnly(ToolDescriptor descriptor):IToolProvider
    {
        public ToolDescriptor Descriptor=>descriptor;
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation,CancellationToken token)=>throw new InvalidOperationException("M0_TOOL_EXECUTION_FORBIDDEN");
    }
    private sealed class DeniedCredentials:FgoPet.Infrastructure.Secrets.ICredentialReader
    {public int Reads;public Task<string?> ReadAsync(string target,CancellationToken token){Reads++;throw new InvalidOperationException("M0_CREDENTIALS_FORBIDDEN");}}
    private sealed class DeniedHttp:HttpMessageHandler
    {public int Sends;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Sends++;throw new InvalidOperationException("M0_HTTP_FORBIDDEN");}}
}

public sealed partial class NativeConversationIntegrationTests
{
    internal static async Task<object> MeasureSupportedOfficialP5Async(IReadOnlyList<IToolProvider> tools)
    {
        var connection = new ModelConnectionSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", maxOutputTokens: 2048);
        Assert.Null(connection.ContextWindowOverride);
        var known = KnownModelContextCatalog.Find(connection);
        Assert.NotNull(known); Assert.Equal(ContextLimitSource.KnownModel, known.Source);
        var budget = PromptBudget.Resolve(known, connection.MaxOutputTokens);
        var settings = new TestSettingsStore(new DialogueSettings(connection, true));
        var route = ModelRouteKey.From(connection); var meter = new FullCatalogMeter();
        var scripted = new ScriptedProvider(PersonaFinal("Synthetic supported-route observation."), PersonaFinal("Synthetic supported-route final."))
            { RequestReceived = request => meter.Dispatch(route, request) };
        await using var host = await TestHost.CreateAsync(new CatalogRouteProvider(scripted, connection), null, new RecordingObserver(),
            settings, tokenMeter: meter, contentResolver: new BoundPersonaResolver(ResolveSourcePersona()), useNative: true,
            skills: true, questions: true, extraTools: tools);
        var result = await host.Orchestrator.SendAsync(SourcePersonaRole, "合成验收：请简短回应。", default, new(ProjectId));
        var expected = tools.Select(t => ModelToolNameMap.GetWireName(t.Descriptor.Name)).ToHashSet(StringComparer.Ordinal);
        expected.UnionWith(new[] { "skill.load", "user.ask", "response.finalize" }.Select(ModelToolNameMap.GetWireName));
        Assert.Equal(38, expected.Count);
        Assert.Equal(ConversationSendStatus.Completed, result.Status); Assert.Equal(2, scripted.Requests.Count);
        Assert.Contains(meter.Measurements, m => expected.SetEquals(m.ToolNames));
        Assert.All(meter.Dispatches, d => Assert.InRange(d.EstimatedInputTokens, 1, budget.InputTokens));
        return new { route, contextSource = known.Source.ToString(), known.WindowTokens, budget.InputTokens,
            outputLimit = connection.MaxOutputTokens, completeToolCount = expected.Count, realProviderCalls = 0,
            syntheticProviderCalls = scripted.Requests.Count, result = result.Status.ToString(),
            measurements = meter.Measurements, dispatches = meter.Dispatches,
            limitation = "Synthetic text admission only; not real-provider semantic quality, image capability, or the user's installed configuration." };
    }

    internal static async Task<object> MeasureCurrentOfficialM0Async(IReadOnlyList<IToolProvider> tools,bool native)
    {
        var connection=new ModelConnectionSettings("deepseek","https://api.deepseek.com/v1","deepseek-flash",maxOutputTokens:2048);
        var settings=new TestSettingsStore(new DialogueSettings(connection,true));var route=ModelRouteKey.From(connection);
        var meter=new FullCatalogMeter();var scripted=new ScriptedProvider(PersonaFinal("Synthetic baseline reply."))
            {RequestReceived=request=>meter.Dispatch(route,request)};
        await using var host=await TestHost.CreateAsync(new LimitedProvider(scripted,16000),null,new RecordingObserver(),
            settings,tokenMeter:meter,contentResolver:new BoundPersonaResolver(ResolveSourcePersona()),useNative:native,skills:true,questions:true,extraTools:tools);
        var result=await host.Orchestrator.SendAsync(SourcePersonaRole,"合成验收：请简短回应。",default,new(ProjectId));
        var expected=tools.Where(t=>native||t.Descriptor.Effect!=ToolEffect.Command)
            .Select(t=>native?ModelToolNameMap.GetWireName(t.Descriptor.Name):t.Descriptor.Name).ToHashSet(StringComparer.Ordinal);
        if(native)expected.UnionWith(new[]{"skill.load","user.ask","response.finalize"}.Select(ModelToolNameMap.GetWireName));
        var visual=Assert.Single(tools,t=>t.Descriptor.Name=="browser.inspect_image");
        Assert.Equal(ToolEffect.ReadOnly,visual.Descriptor.Effect);
        Assert.Contains(native?ModelToolNameMap.GetWireName("browser.inspect_image"):"browser.inspect_image",expected);
        Assert.Equal(native?38:23,expected.Count);
        var basis=meter.Measurements.Where(m=>expected.SetEquals(m.ToolNames)).ToArray();Assert.NotEmpty(basis);
        Assert.Equal(native?ConversationSendStatus.Failed:ConversationSendStatus.Completed,result.Status);
        Assert.Equal(native?0:1,scripted.Requests.Count);
        if(native)Assert.True(basis.Max(m=>m.EstimatedInputTokens)>16000);
        else Assert.All(meter.Dispatches,m=>Assert.True(m.EstimatedInputTokens<=16000));
        return new{native,route,outputLimit=2048,inputLimit=16000,warningLimit=9600,
            countKind="Estimated; actual production meter and shared wire projection; synthetic usage not recorded",
            scope="All current registered tool descriptors, source Persona, host and Native control contracts, synthetic minimal input and empty history",
            tools=expected.Count,result=result.Status.ToString(),realProviderCalls=0,syntheticProviderCalls=scripted.Requests.Count,
            maximumBaseEstimate=basis.Max(m=>m.EstimatedInputTokens),measurements=meter.Measurements,dispatches=meter.Dispatches};
    }
}
