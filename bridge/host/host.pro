# The omasharp host: one small Qt program that runs Main.qml with the app's C#
# Backend exposed as `backend`. Build: qmake6 host.pro TARGET=<app> [APP_QRC=<qml.qrc>]
QT += core gui qml quick quickcontrols2
CONFIG += c++17 release
TEMPLATE = app
isEmpty(TARGET): TARGET = omasharp-host
SOURCES += main.cpp devhost.cpp theme.cpp textdump.cpp drawview.cpp
HEADERS += omasharp.h
LIBS += -ldl
QMAKE_LFLAGS += -Wl,-rpath,\'\$\$ORIGIN\'
!isEmpty(APP_QRC): RESOURCES += $$APP_QRC
# Dev mode: the app's managed Main finds omasharp_host_run in this executable.
QMAKE_LFLAGS += -Wl,--export-dynamic-symbol=omasharp_host_run
