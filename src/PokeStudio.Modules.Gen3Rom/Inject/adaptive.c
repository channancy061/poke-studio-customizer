// Poke Studio Customizer: the adaptive boss slot for unmodified Pokémon FireRed (US 1.0) and Emerald (US) ROMs.
// MIT licence (this project). Our own code: it only calls the game's public routines (create a Pokémon, read and set
// its data) and reads the game's type chart.
//
// After a trainer's team is created, a boss listed in our table swaps one team member for whichever candidate best
// counters the player's current party. The code is position-independent (no globals, no strings, game routines are
// reached through their fixed addresses), so the app can place it in any free space of the ROM.

typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned int u32;
typedef signed int s32;

struct Pokemon;

// game routines (fixed addresses per game version, given to the linker)
extern void CreateMon(struct Pokemon *mon, u16 species, u8 level, u8 fixedIV, u8 hasFixedPersonality, u32 fixedPersonality, u8 otIdType, u32 fixedOtId)
    __attribute__((long_call));
extern u32 GetMonData2(struct Pokemon *mon, s32 field) __attribute__((long_call));
extern void SetMonData(struct Pokemon *mon, s32 field, const void *data) __attribute__((long_call));
extern void SetMonMoveSlot(struct Pokemon *mon, u16 move, u8 slot) __attribute__((long_call));

// game data (addresses passed in by the build, per game version)
#define ENEMY_PARTY ((u8 *)GAME_ENEMY_PARTY)
#define PLAYER_PARTY ((u8 *)GAME_PLAYER_PARTY)
#define PLAYER_PARTY_COUNT (*(volatile u8 *)GAME_PLAYER_PARTY_COUNT)
#define TRAINER_A (*(volatile u16 *)GAME_TRAINER_A)
#define SPECIES_INFO ((const u8 *)GAME_SPECIES_INFO)
#define TYPE_CHART ((const u8 *)GAME_TYPE_CHART)

#define POKEMON_SIZE 100
#define SPECIES_INFO_SIZE 28
#define SPECIES_TYPES 6

#define MON_DATA_SPECIES 11
#define MON_DATA_HELD_ITEM 12
#define MON_DATA_IS_EGG 45
#define MON_DATA_HP 57
#define OT_ID_RANDOM_NO_SHINY 2

#define TYPE_FORESIGHT 0xFE
#define TYPE_ENDTABLE 0xFF

// Table written by the app:  u32 entryCount, then per entry:
//   u16 trainer, u8 flexSlot, u8 candidateCount, then candidateCount × Candidate (14 bytes)
struct Candidate
{
    u16 species;
    u8 level;
    u8 iv;          // 0–31, for every stat
    u16 heldItem;   // 0 = none
    u16 moves[4];   // all 0 = the moves it learns by its level
};

#define POKEMON(base, i) ((struct Pokemon *)((base) + POKEMON_SIZE * (i)))

static u8 TypeOf(u16 species, int which)
{
    return SPECIES_INFO[species * SPECIES_INFO_SIZE + SPECIES_TYPES + which];
}

// the chart's multipliers are ×0, ×0.5, ×1 and ×2 (stored as 0, 5, 10, 20): shifts, no division (the CPU has none)
static int Apply(int mult, u8 chart)
{
    if (chart == 0)
        return 0;
    if (chart == 5)
        return mult >> 1;
    if (chart == 20)
        return mult << 1;
    return mult;
}

// effectiveness ×100 of one attacking type against a Pokémon with these two types (the game's own chart)
static int Effect(u8 attack, u8 def1, u8 def2)
{
    int mult = 100;
    const u8 *row;
    for (row = TYPE_CHART; row[0] != TYPE_ENDTABLE; row += 3)
    {
        if (row[0] == TYPE_FORESIGHT)
            continue;
        if (row[0] != attack)
            continue;
        if (row[1] == def1)
            mult = Apply(mult, row[2]);
        if (row[1] == def2 && def2 != def1)
            mult = Apply(mult, row[2]);
    }
    return mult;
}

static int Best(u8 a1, u8 a2, u8 d1, u8 d2)
{
    int x = Effect(a1, d1, d2);
    int y = a2 != a1 ? Effect(a2, d1, d2) : 0;
    return x > y ? x : y;
}

// how well a species does against the player's party: its best hit minus their best hit, summed over the party
static int Score(u16 species)
{
    int i, score = 0;
    u8 s1 = TypeOf(species, 0), s2 = TypeOf(species, 1);
    for (i = 0; i < PLAYER_PARTY_COUNT && i < 6; i++)
    {
        struct Pokemon *mon = POKEMON(PLAYER_PARTY, i);
        u16 p = (u16)GetMonData2(mon, MON_DATA_SPECIES);
        u8 p1, p2;
        if (p == 0 || GetMonData2(mon, MON_DATA_IS_EGG) || GetMonData2(mon, MON_DATA_HP) == 0)
            continue;
        p1 = TypeOf(p, 0);
        p2 = TypeOf(p, 1);
        score += Best(s1, s2, p1, p2) - Best(p1, p2, s1, s2);
    }
    return score;
}

// Called right after the trainer's team was made. table: the app's table (see above).
void PokeStudio_AdaptiveSlot(const u8 *table)
{
    u32 entries = *(const u32 *)table;
    const u8 *at = table + 4;
    u16 trainer = TRAINER_A;
    u32 e;

    for (e = 0; e < entries; e++)
    {
        u16 id = (u16)(at[0] | (at[1] << 8));
        u8 flex = at[2];
        u8 count = at[3];
        const struct Candidate *c = (const struct Candidate *)(at + 4);
        at += 4 + count * sizeof(struct Candidate);
        if (id != trainer || flex >= 6)
            continue;

        {
            struct Pokemon *mon = POKEMON(ENEMY_PARTY, flex);
            u16 current = (u16)GetMonData2(mon, MON_DATA_SPECIES);
            int best = current != 0 ? Score(current) : -100000;
            int pick = -1, i;
            for (i = 0; i < count; i++)
            {
                int s = Score(c[i].species);
                if (s > best)
                {
                    best = s;
                    pick = i;
                }
            }

            if (pick >= 0)
            {
                const struct Candidate *k = &c[pick];
                CreateMon(mon, k->species, k->level, k->iv, 0, 0, OT_ID_RANDOM_NO_SHINY, 0);
                if (k->heldItem != 0)
                    SetMonData(mon, MON_DATA_HELD_ITEM, &k->heldItem);
                if (k->moves[0] | k->moves[1] | k->moves[2] | k->moves[3])
                {
                    for (i = 0; i < 4; i++)
                        SetMonMoveSlot(mon, k->moves[i], (u8)i);
                }
            }
        }
        return;
    }
}
