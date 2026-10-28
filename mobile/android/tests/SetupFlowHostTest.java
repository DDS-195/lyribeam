package com.local.lyricbridge;
public final class SetupFlowHostTest {
    private static int count;
    private static void check(String name,boolean condition) {
        if (!condition) throw new AssertionError(name);
        count++;
    }
    public static void main(String[] args) {
        check("Fresh install shows setup",!SetupFlow.initiallyCompleted(null,false));
        check("Existing configured user keeps daily home",SetupFlow.initiallyCompleted(null,true));
        check("Interrupted first setup is not silently completed",!SetupFlow.initiallyCompleted(Boolean.FALSE,true));
        check("Completed setup remains complete if computer was removed",SetupFlow.initiallyCompleted(Boolean.TRUE,false));
        check("Completed setup persists across reopening",SetupFlow.initiallyCompleted(Boolean.TRUE,true));
        check("Unconfigured install remains on setup",!SetupFlow.initiallyCompleted(Boolean.FALSE,false));
        for (int bits=0;bits<8;bits++)
            check("Setup prerequisites "+bits,SetupFlow.canFinish((bits&1)!=0,(bits&2)!=0,(bits&4)!=0)==(bits==7));
        for (int bits=0;bits<16;bits++)
            check("Available source prerequisites "+bits,SetupFlow.canFinish((bits&1)!=0,(bits&2)!=0,(bits&4)!=0,(bits&8)!=0)
                ==((bits&3)!=0&&(bits&4)!=0&&(bits&8)!=0));
        check("SDK access does not bypass Bluetooth or computer",!SetupFlow.canFinish(false,true,false,true)&&!SetupFlow.canFinish(false,true,true,false));
        check("No live approved source still requires notification access",!SetupFlow.canFinish(false,false,true,true));
        System.out.println("{\"success\":true,\"checks\":"+count+"}");
    }
}
