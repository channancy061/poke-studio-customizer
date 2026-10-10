// Invented test fixture.
u32 GetCurrentLevelCap(void)
{
    static const u32 sLevelCapFlagMap[][2] =
    {
        {FLAG_BADGE01_GET, 12},
        {FLAG_BADGE02_GET, 18},
        {FLAG_BADGE03_GET, 25},
        {FLAG_IS_CHAMPION, 40},
    };

    return MAX_LEVEL;
}
