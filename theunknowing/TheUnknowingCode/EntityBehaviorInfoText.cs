using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace TheUnknowing
{
    // Entities have no built-in JSON "description" field the way items/blocks do - GetInfoText is
    // purely an aggregation of whatever each behavior contributes. Reads its lang key from JSON
    // ("langCode") rather than hardcoding one entity's text, so any entity can reuse it.
    public class EntityBehaviorInfoText : EntityBehavior
    {
        private string langCode = "";

        public EntityBehaviorInfoText(Entity entity) : base(entity)
        {
        }

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            langCode = attributes["langCode"].AsString("");
        }

        public override void GetInfoText(StringBuilder infotext)
        {
            base.GetInfoText(infotext);
            if (langCode != "") infotext.AppendLine(Lang.Get(langCode));
        }

        public override string PropertyName() => "theunknowing:infotext";
    }
}
