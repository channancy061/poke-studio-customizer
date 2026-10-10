// Invented test fixture.

const struct MoveInfo gMovesInfo[MOVES_COUNT_ALL] =
{
    [MOVE_NONE] =
    {
        .name = COMPOUND_STRING("-"),
        .description = COMPOUND_STRING(""),
        .effect = EFFECT_NONE,
        .power = 0,
        .type = TYPE_NORMAL,
        .accuracy = 0,
        .pp = 0,
        .target = TARGET_SELECTED,
        .priority = 0,
        .category = DAMAGE_CATEGORY_PHYSICAL,
    },

    [MOVE_TACKLE] =
    {
        .name = COMPOUND_STRING("Tackle"),
        .description = COMPOUND_STRING(
            "Charges the foe with a\n"
            "full-body tackle."),
        .effect = EFFECT_HIT,
        .power = B_UPDATED_MOVE_DATA >= GEN_7 ? 40 : 35,
        .type = TYPE_NORMAL,
        .accuracy = B_UPDATED_MOVE_DATA >= GEN_5 ? 100 : 95,
        .pp = 35,
        .target = TARGET_SELECTED,
        .priority = 0,
        .category = DAMAGE_CATEGORY_PHYSICAL,
        .makesContact = TRUE,
    },

    [MOVE_GROWL] =
    {
        .name = COMPOUND_STRING("Growl"),
        .description = COMPOUND_STRING(
            "Growls cutely to reduce\n"
            "the foe's Attack."),
        .effect = EFFECT_STAT_CHANGE,
        .power = 0,
        .type = TYPE_NORMAL,
        .accuracy = 100,
        .pp = 40,
        .target = TARGET_BOTH,
        .priority = 0,
        .category = DAMAGE_CATEGORY_STATUS,
    },

    [MOVE_VINE_LASH] =
    {
        .name = COMPOUND_STRING("Vine Lash"),
        .description = COMPOUND_STRING(
            "Strikes the foe with\n"
            "slender vines."),
        .effect = EFFECT_HIT,
        .power = 45,
        .type = TYPE_GRASS,
        .accuracy = 100,
    #if B_UPDATED_MOVE_DATA >= GEN_6
        .pp = 25,
    #else
        .pp = 10,
    #endif
        .target = TARGET_SELECTED,
        .priority = 0,
        .category = DAMAGE_CATEGORY_PHYSICAL,
        .makesContact = TRUE,
    },

    [MOVE_SLEEP_DUST] =
    {
        .name = COMPOUND_STRING("Sleep Dust"),
        .description = COMPOUND_STRING(
            "Scatters a dust that\n"
            "may make the foe sleep."),
        .effect = EFFECT_NON_VOLATILE_STATUS,
        .power = 0,
        .type = TYPE_GRASS,
        .accuracy = 75,
        .pp = 15,
        .target = TARGET_SELECTED,
        .priority = 0,
        .category = DAMAGE_CATEGORY_STATUS,
    },

    [MOVE_SEED_BOMB] =
    {
        .name = COMPOUND_STRING("Seed Bomb"),
        .description = COMPOUND_STRING(
            "Fires a barrage of hard\n"
            "seeds at the foe."),
        .effect = EFFECT_HIT,
        .power = 80,
        .type = TYPE_GRASS,
        .accuracy = 100,
        .pp = 15,
        .target = TARGET_SELECTED,
        .priority = 0,
        .category = DAMAGE_CATEGORY_PHYSICAL,
    },

    [MOVE_EMBER_SNAP] =
    {
        .name = COMPOUND_STRING("Ember Snap"),
        .description = COMPOUND_STRING(
            "A fiery bite that may\n"
            "leave a burn."),
        .effect = EFFECT_HIT,
        .power = 60,
        .type = TYPE_FIRE,
        .accuracy = 100,
        .pp = 25,
        .target = TARGET_SELECTED,
        .priority = 0,
        .category = DAMAGE_CATEGORY_PHYSICAL,
        .makesContact = TRUE,
    },

    [MOVE_FROST_BREATH] =
    {
        .name = COMPOUND_STRING("Frost Breath"),
        .description = COMPOUND_STRING(
            "An icy breath that always\n"
            "lands a critical hit."),
        .effect = EFFECT_HIT,
        .power = B_UPDATED_MOVE_DATA >= GEN_6 ? 60 : 40,
        .type = B_UPDATED_MOVE_TYPES >= GEN_5 ? TYPE_ICE : TYPE_WATER,
        .accuracy = 90,
        .pp = 10,
        .target = TARGET_SELECTED,
        .priority = 0,
        .category = DAMAGE_CATEGORY_SPECIAL,
    },

    [MOVE_CHARM] =
    {
        .name = COMPOUND_STRING("Charm"),
        .description = COMPOUND_STRING(
            "Charms the foe and\n"
            "sharply lowers Attack."),
        .effect = EFFECT_STAT_CHANGE,
        .power = 0,
        .type = B_UPDATED_MOVE_TYPES >= GEN_6 ? TYPE_FAIRY : TYPE_NORMAL,
        .accuracy = 100,
        .pp = 20,
        .target = TARGET_SELECTED,
        .priority = 0,
        .category = DAMAGE_CATEGORY_STATUS,
    },

    [MOVE_SOLAR_RAY] =
    {
        .name = COMPOUND_STRING("Solar Ray"),
        .description = COMPOUND_STRING(
            "Gathers light, then fires\n"
            "a strong ray next turn."),
        .effect = EFFECT_TWO_TURNS_ATTACK,
        .power = 120,
        .type = TYPE_GRASS,
        .accuracy = 100,
        .pp = 10,
        .target = TARGET_SELECTED,
        .priority = 0,
        .category = DAMAGE_CATEGORY_SPECIAL,
    },

    [MOVE_PROTECT] =
    {
        .name = COMPOUND_STRING("Protect"),
        .description = COMPOUND_STRING(
            "Evades attack, but may\n"
            "fail if used in a row."),
        .effect = EFFECT_PROTECT,
        .power = 0,
        .type = TYPE_NORMAL,
        .accuracy = 0,
        .pp = 10,
        .target = TARGET_USER,
        .priority = 4,
        .category = DAMAGE_CATEGORY_STATUS,
    },
};
