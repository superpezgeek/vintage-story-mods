using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace TheUnknowing
{
    // Vintage Story entities have no built-in JSON "description" field the way items/blocks do -
    // the hover tooltip (GetInfoText) is purely an aggregation of whatever each behavior
    // contributes (health, generation, etc. - see EntityAgent/Entity.GetInfoText in vsapi). This
    // behavior exists solely to append one Lang.Get'd line of flavor text below the entity's
    // name. Reads its lang key from JSON ("langCode") rather than hardcoding stormcloud's text,
    // in case a future entity in this mod wants the same treatment.
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
