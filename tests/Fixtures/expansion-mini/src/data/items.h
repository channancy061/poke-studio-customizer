// Invented test fixture.
#define ITEM_NAME(str) COMPOUND_STRING_SIZE_LIMIT(str, ITEM_NAME_LENGTH)

const struct ItemInfo gItemsInfo[] =
{
    [ITEM_NONE] =
    {
        .name = ITEM_NAME("????????"),
        .price = 0,
    },

    [ITEM_SUN_STONE] =
    {
        .name = ITEM_NAME("Sun Stone"),
        .price = (I_PRICE >= GEN_7) ? 3000 : 2100,
        .description = COMPOUND_STRING(
            "Makes certain\n"
            "species evolve."),
        .pocket = POCKET_ITEMS,
    },

    [ITEM_MIRACLE_SEED] =
    {
        .name = ITEM_NAME("Miracle Seed"),
        .price = 1000,
        .holdEffect = HOLD_EFFECT_TYPE_POWER,
        .holdEffectParam = 20,
        .description = COMPOUND_STRING(
            "A hold item that\n"
            "raises the power of\n"
            "Grass-type moves."),
        .pocket = POCKET_ITEMS,
    },

    [ITEM_ORAN_BERRY] =
    {
        .name = ITEM_NAME("Oran Berry"),
    #if I_PRICE >= GEN_8
        .price = 80,
    #else
        .price = 20,
    #endif
        .holdEffectParam = 10,
        .description = COMPOUND_STRING(
            "A hold item that\n"
            "restores 10 HP in\n"
            "battle."),
        .pocket = POCKET_BERRIES,
    },
};
