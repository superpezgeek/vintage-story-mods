using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace TheUnknowing
{
    public class TheUnknowingModSystem : ModSystem
    {
        private const string ConfigFilename = "TheUnknowing.json";

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            UnknowingConfig config = api.LoadModConfig<UnknowingConfig>(ConfigFilename) ?? new UnknowingConfig();
            api.StoreModConfig(config, ConfigFilename);

            var stormManager = new UnknowingStormManager(api, config);
            api.Event.RegisterGameTickListener(_ => stormManager.OnGameTick(), 10000);
            api.Event.RegisterGameTickListener(_ => stormManager.OnSpawnTick(), (int)(config.EnemySpawnIntervalSeconds * 1000));

            api.ChatCommands.Create("unknowing-storm")
                .WithAlias(new[] { "unknowingstorm" })
                .WithDescription("Summons an Unknowing Storm over every land claim owned by the given player (offline is fine) - erases the claim(s) and starts the storm.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.Word("playerName"))
                .HandleWith(args => stormManager.StartStorm((string)args[0]));

            api.ChatCommands.Create("unknowing-storm-clear")
                .WithDescription("Debug/testing only: despawns every enemy every tracked storm owns and clears all storm state. Nothing in the real lifecycle calls this.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => stormManager.ClearAllStorms());
        }
    }
}
