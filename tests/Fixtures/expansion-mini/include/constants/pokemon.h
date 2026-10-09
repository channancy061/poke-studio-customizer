enum __attribute__((packed)) Type
{
    TYPE_NONE = 0,
    TYPE_NORMAL = 1,
    TYPE_FIRE = 2,
    TYPE_GRASS = 3,
    TYPE_FAIRY = 4,
    TYPE_ICE = 5,
    TYPE_GHOST = 6,
    NUMBER_OF_MON_TYPES
};

enum EggGroup
{
    EGG_GROUP_NONE,
    EGG_GROUP_MONSTER,
    EGG_GROUP_GRASS,
    EGG_GROUP_FIELD,
    EGG_GROUP_AMORPHOUS,
};

enum GrowthRate
{
    GROWTH_MEDIUM_FAST,
    GROWTH_MEDIUM_SLOW,
    GROWTH_FAST,
    GROWTH_SLOW,
};

#define MON_MALE       0x00
#define MON_FEMALE     0xFE
#define MON_GENDERLESS 0xFF
