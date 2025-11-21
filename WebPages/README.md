# To use old version just rename "unity-plotter_BLE" --> "unity-plotter" and the current "unity-plotter" --> "unity-plotter_ESP"

## Idea
Use ESP-NOW instead of BLE to communicate with the haptic control unit 

## New Architecture
Unity -> WebSocket -> Python Script -> Serial (USB) -> Gateway ESP32 -> ESP-NOW -> Haptic 

## Organization 
- ESP_Setup contains the files needed to configure ESP-NOW
- unity-plotter is the main folder (running esp-now)
- unity-plotter_BLE is the old version running BLE 
