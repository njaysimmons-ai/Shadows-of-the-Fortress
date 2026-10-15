using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.GameTicking.Components;
using Robust.Shared.Console;

namespace Content.Server._Hollow;

/// <summary>
/// Skips every running Hollow Fog rule to its next phase (fog, siren, Otherworld, fog...).
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed partial class HollowAdvanceCommand : LocalizedEntityCommands
{
    [Dependency] private HollowFogRuleSystem _hollow = default!;

    public override string Command => "hollowadvance";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var advanced = 0;
        var query = EntityManager.EntityQueryEnumerator<HollowFogRuleComponent, ActiveGameRuleComponent>();
        while (query.MoveNext(out _, out var rule, out _))
        {
            _hollow.AdvancePhase(rule);
            var phase = rule.Phase;
            shell.WriteLine(Loc.GetString("cmd-hollowadvance-advanced", ("phase", phase.ToString())));
            advanced++;
        }

        if (advanced == 0)
            shell.WriteError(Loc.GetString("cmd-hollowadvance-no-rule"));
    }
}
