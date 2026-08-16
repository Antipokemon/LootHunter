# LootHunter

LootHunter automatically farms ordinary open-world monster drops from a quantity list.

## Install

1. Run `/xlsettings`, open **Experimental → Custom Plugin Repositories**, and add these five URLs:

   ```text
   https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json
   https://puni.sh/api/repository/veyn
   https://love.puni.sh/ment.json
   https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json
   https://raw.githubusercontent.com/Antipokemon/LootHunter/main/repo.json
   ```

2. Save the settings.
3. Run `/xlplugins`, then install and enable **Lifestream**, **vnavmesh**, **Wrath Combo**, **BossMod Reborn**, and **LootHunter**.

## Use

1. Log in on a combat job, leave a normal inventory slot free, and stay outside duties.
2. Run `/loothunter`.
3. Enter a name and click **New list**.
4. Choose whether quantities mean an inventory total or an additional amount to gather.
5. Use **Select monster drop** to add items, then set each quantity and enable it.
6. Click **Start farming**.

LootHunter uses unlocked aetherytes to travel, vnavmesh for routes, Wrath Combo for combat actions, and BossMod Reborn AI for combat movement and area-attack avoidance. Flight is optional under **Settings**.

Use **Stop** to end a run. The **Session** section shows the current state and any problem that prevents farming.
