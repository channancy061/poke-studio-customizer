#ifdef __INTELLISENSE__
const struct SpeciesInfo gSpeciesInfoGen1[] =
{
#endif

#if P_FAMILY_SPROUTLE
    [SPECIES_SPROUTLE] =
    {
        .baseHP        = 45,
        .baseAttack    = 49,
        .baseDefense   = 49,
        .baseSpeed     = 45,
        .baseSpAttack  = 65,
        .baseSpDefense = 65,
        .types = (P_UPDATED_TYPES >= GEN_6) ? MON_TYPES(TYPE_GRASS, TYPE_FAIRY) : MON_TYPES(TYPE_GRASS),
        .catchRate = 45,
        .expYield = 64,
        .genderRatio = PERCENT_FEMALE(12.5),
        .growthRate = GROWTH_MEDIUM_SLOW,
        .eggGroups = MON_EGG_GROUPS(EGG_GROUP_MONSTER, EGG_GROUP_GRASS),
        .abilities = { ABILITY_OVERGROW, ABILITY_NONE, ABILITY_CHLOROPHYLL },
        .speciesName = _("Sproutle"),
        .categoryName = _("Seedling"),
        .description = COMPOUND_STRING(
            "It naps in sunny meadows.\n"
            "The bud on its back swells a\n"
            "little more each day."),
        SHADOW(1, -1, SHADOW_SIZE_S)
        FOOTPRINT(Sproutle)
        .levelUpLearnset = sSproutleLevelUpLearnset,
        .teachableLearnset = sSproutleTeachableLearnset,
        .eggMoveLearnset = sSproutleEggMoveLearnset,
        .evolutions = EVOLUTION({EVO_LEVEL, 16, SPECIES_BLOOMTLE}),
    },

#if P_UPDATED_STATS >= GEN_6
    #define BLOOMTLE_SP_ATK   90
#else
    #define BLOOMTLE_SP_ATK   80
#endif

    [SPECIES_BLOOMTLE] =
    {
        .baseHP        = 60,
        .baseAttack    = 62,
        .baseDefense   = 63,
        .baseSpeed     = 60,
        .baseSpAttack  = BLOOMTLE_SP_ATK,
        .baseSpDefense = 80,
        .types = MON_TYPES(TYPE_GRASS),
        .catchRate = 45,
        .expYield = 142,
        .itemRare = ITEM_MIRACLE_SEED,
        .genderRatio = PERCENT_FEMALE(12.5),
        .growthRate = GROWTH_MEDIUM_SLOW,
        .eggGroups = MON_EGG_GROUPS(EGG_GROUP_MONSTER, EGG_GROUP_GRASS),
    #if P_UPDATED_ABILITIES >= GEN_4
        .abilities = { ABILITY_OVERGROW, ABILITY_LEAF_GUARD, ABILITY_CHLOROPHYLL },
    #else
        .abilities = { ABILITY_OVERGROW, ABILITY_NONE, ABILITY_CHLOROPHYLL },
    #endif
        .speciesName = _("Bloomtle"),
        .categoryName = _("Seedling"),
        .description = COMPOUND_STRING(
            "Its petals glow faintly at dusk."),
        .levelUpLearnset = sBloomtleLevelUpLearnset,
        .teachableLearnset = sBloomtleTeachableLearnset,
        .eggMoveLearnset = sSproutleEggMoveLearnset,
        .evolutions = EVOLUTION({EVO_ITEM, ITEM_SUN_STONE, SPECIES_SPROUTLE_FROST, CONDITIONS({IF_TIME, TIME_NIGHT})},
                                {EVO_LEVEL, 36, SPECIES_EMBERPUP}),
    },

#if P_FROST_FORMS
    [SPECIES_SPROUTLE_FROST] =
    {
        .baseHP        = 50,
        .baseAttack    = 45,
        .baseDefense   = 55,
        .baseSpeed     = 40,
        .baseSpAttack  = 70,
        .baseSpDefense = 70,
        .types = MON_TYPES(TYPE_GRASS, TYPE_ICE),
        .catchRate = 30,
        .expYield = 70,
        .genderRatio = MON_GENDERLESS,
        .growthRate = GROWTH_SLOW,
        .eggGroups = MON_EGG_GROUPS(EGG_GROUP_GRASS),
        .abilities = { ABILITY_OVERGROW, ABILITY_NONE, ABILITY_NONE },
        .speciesName = _("Sproutle"),
        .categoryName = _("Frostbud"),
        .description = gSproutlePokedexText,
        .levelUpLearnset = sSproutleLevelUpLearnset,
        .teachableLearnset = sSproutleFrostTeachableLearnset,
        .eggMoveLearnset = sSproutleEggMoveLearnset,
        .teachingType = ALL_TEACHABLES,
    },
#endif //P_FROST_FORMS
#endif //P_FAMILY_SPROUTLE

#if P_FAMILY_EMBERPUP
    [SPECIES_EMBERPUP] =
    {
        .baseHP        = 39,
        .baseAttack    = 52,
        .baseDefense   = 43,
        .baseSpeed     = 65,
        .baseSpAttack  = 60,
        .baseSpDefense = 50,
        .types = MON_TYPES(TYPE_FIRE),
        .catchRate = 45,
        .expYield = 62,
        .genderRatio = PERCENT_FEMALE(12.5),
        .growthRate = GROWTH_MEDIUM_SLOW,
        .eggGroups = MON_EGG_GROUPS(EGG_GROUP_MONSTER, EGG_GROUP_FIELD),
        .abilities = { ABILITY_BLAZE, ABILITY_NONE, ABILITY_NONE },
        .speciesName = _("Emberpup"),
        .categoryName = _("Ember"),
        .description = COMPOUND_STRING(
            "Its tail flickers brighter\n"
            "when it is happy."),
        .levelUpLearnset = sEmberpupLevelUpLearnset,
        .teachableLearnset = sEmberpupTeachableLearnset,
        .eggMoveLearnset = sEmberpupEggMoveLearnset,
        .evolutions = EVOLUTION({EVO_LEVEL, 20, SPECIES_SPROUTLE},
                            #if P_GEN_4_CROSS_EVOS
                                {EVO_ITEM, ITEM_SUN_STONE, SPECIES_BLOOMTLE},
                            #endif
                                {EVO_TRADE, 0, SPECIES_SPROUTLE_FROST}),
    },
#endif //P_FAMILY_EMBERPUP

#if P_FAMILY_GHOSTLING
    [SPECIES_GHOSTLING] =
    {
        .baseHP        = 30,
        .baseAttack    = 35,
        .baseDefense   = 30,
        .baseSpeed     = 80,
        .baseSpAttack  = 100,
        .baseSpDefense = 35,
        .types = MON_TYPES(TYPE_GHOST),
        .catchRate = 190,
        .expYield = 62,
        .genderRatio = PERCENT_FEMALE(50),
        .growthRate = GROWTH_MEDIUM_SLOW,
        .eggGroups = MON_EGG_GROUPS(EGG_GROUP_AMORPHOUS),
        .abilities = { ABILITY_LEVITATE, ABILITY_NONE, ABILITY_NONE },
        .speciesName = _("Ghostling"),
        .categoryName = _("Wisp"),
        .description = COMPOUND_STRING(
            "It drifts through walls at night."),
        .levelUpLearnset = sGhostlingLevelUpLearnset,
        .teachableLearnset = sGhostlingTeachableLearnset,
    },
#endif //P_FAMILY_GHOSTLING

#ifdef __INTELLISENSE__
};
#endif
