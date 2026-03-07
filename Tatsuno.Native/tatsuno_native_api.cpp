#include "pch.h"
#include "tatsuno_native_api.h"

int tatsuno_protocol_version()
{
    return 1;
}

void tatsuno_fill_default_info(tatsuno_controller_info* info)
{
    if (info == nullptr)
    {
        return;
    }

    info->nozzles_count = 3;
    info->active_nozzle = 0;
    info->condition = 0;
    info->controllability = 2;
    info->current_volume_raw = 0;
    info->current_amount_raw = 0;
    info->current_unit_price_raw = 0;
}
