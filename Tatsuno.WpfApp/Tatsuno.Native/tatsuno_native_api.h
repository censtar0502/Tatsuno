#pragma once

#ifdef TATSUNONATIVE_EXPORTS
#define TATSUNO_API __declspec(dllexport)
#else
#define TATSUNO_API __declspec(dllimport)
#endif

extern "C"
{
    struct tatsuno_controller_info
    {
        int nozzles_count;
        int active_nozzle;
        int condition;
        int controllability;
        int current_volume_raw;
        int current_amount_raw;
        int current_unit_price_raw;
    };

    TATSUNO_API int tatsuno_protocol_version();
    TATSUNO_API void tatsuno_fill_default_info(tatsuno_controller_info* info);
}
