using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class IndependentClueCommitTests
{
    [Theory] [InlineData("body")] [InlineData("coverage")] [InlineData("scope")] [InlineData("revoked")] [InlineData("source")]
    public void Invalidated_authority_or_provenance_cannot_commit_a_clue(string invalid)
    {
        var f = new Fixture(); var ticket = f.Ticket(); var material = f.Material;
        if(invalid=="body") material=[material[0], material[1] with { Text="fabricated" }];
        if(invalid=="coverage") material=[material[0], material[1] with { Coverage=material[1].Coverage with { IncludedScalars=2 } }];
        if(invalid=="scope") ticket=ticket with { Scope=new("mash","other") };
        if(invalid=="revoked") f.Owner.Configure(f.Scope,f.Owner.State(f.Scope).Revision,new());
        if(invalid=="source") f.Sources.Current=false;
        Assert.ThrowsAny<Exception>(()=>f.Owner.CommitRecurrenceClues(ticket,f.Drafts,material,f.Sources.Heads));
        f.Sources.Current=true;Assert.Empty(f.Owner.RecurrenceClues(f.Scope));Assert.Empty(f.Owner.List(f.Scope,true));
    }
    [Fact]
    public void Independent_commit_does_not_acknowledge_Self_and_replay_cannot_renew_an_expired_clue()
    {
        var f=new Fixture();var state=f.Owner.State(f.Scope);var ticket=f.Ticket();
        var receipt=f.Owner.CommitRecurrenceClues(ticket,f.Drafts,f.Material,f.Sources.Heads);
        Assert.Equal(state,f.Owner.State(f.Scope));Assert.Null(f.Owner.Receipt(f.Scope,ticket.Id));
        Assert.Single(f.Owner.RecurrenceClues(f.Scope));Assert.Empty(f.Owner.Query(f.Scope,"").Items);
        f.Clock.Now=f.Clock.Now.AddDays(14);Assert.Empty(f.Owner.RecurrenceClues(f.Scope));
        Assert.Equal(receipt.OperationId,f.Owner.CommitRecurrenceClues(f.Ticket(),f.Drafts,f.Material,f.Sources.Heads).OperationId);
        Assert.Empty(f.Owner.RecurrenceClues(f.Scope));
    }
    private sealed class Fixture
    {
        public readonly ConversationScope Scope=new("mash","p");
        public readonly ReviewWorkQueueTests.Timeline Sources=new();public readonly Time Clock=new();
        public readonly UnderstandingService Owner;public readonly ReviewOriginal[] Material;public readonly RecurrenceClueDraft[] Drafts;
        public Fixture()
        {
            Sources.Add(2);for(var n=0;n<2;n++)Sources.Heads[n]=Sources.Heads[n] with{Source=Sources.Heads[n].Source with{Fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("xxxxxxxxxx")))}};
            var db=new RuntimeDatabase(Path.Combine(Path.GetTempPath(),"fgo-clue-commit-"+Guid.NewGuid().ToString("N"),"runtime.db"),pooling:false);
            new RuntimeDatabaseMigrator(db).Migrate();Owner=new(db,Sources,Clock);
            Owner.Configure(Scope,0,new(UnderstandingMode.Bounded,ReadSources:true,SaveCandidates:true,ExternalProcessing:true));
            Material=Sources.Heads.Select(h=>new ReviewOriginal(h.Source,"xxxxxxxxxx",new(h.Source,0,10,10))).ToArray();
            Drafts=[new("observation","Assistant expressed a synthetic choice.",[new(Material[1].Source,Material[1].Text)])];
        }
        public UnderstandingTicket Ticket()=>Owner.Begin(Scope,"event",Material.Select(o=>o.Source).ToArray(),coverage:Material.Select(o=>o.Coverage).ToArray());
    }
    private sealed class Time:TimeProvider{public DateTimeOffset Now=DateTimeOffset.Parse("2026-10-27T00:00:00Z");public override DateTimeOffset GetUtcNow()=>Now;}
}
