package com.local.lyricbridge;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.os.Binder;
import android.os.Bundle;

/** Explicit, authenticated calls only; no song database or file access is exported. */
public final class IntegrationProvider extends ContentProvider {
    @Override public boolean onCreate(){return true;}
    private String caller(Bundle extras) {
        String[] packages=getContext().getPackageManager().getPackagesForUid(Binder.getCallingUid());
        // Shared UIDs cannot unambiguously identify which sibling sent a snapshot.
        if(packages==null||packages.length!=1)throw new SecurityException("A unique caller package is required");
        String pkg=packages[0],attributed=getCallingPackage();
        if(attributed!=null&&!attributed.equals(pkg))throw new SecurityException("Caller package mismatch");
        if(extras!=null&&extras.containsKey("packageName")) {
            Object claimed=extras.get("packageName");
            if(!(claimed instanceof String)||!pkg.equals(claimed))throw new SecurityException("Claimed package mismatch");
        }
        return pkg;
    }
    @Override public Bundle call(String method,String arg,Bundle extras) {
        String pkg=caller(extras);IntegrationStore.noteSource(getContext(),pkg);
        if("status".equals(method)||"requestAccess".equals(method))return IntegrationStore.status(getContext(),pkg);
        if("publish".equals(method))return IntegrationStore.publish(getContext(),pkg,extras);
        if("clear".equals(method))return IntegrationStore.clear(getContext(),pkg);
        Bundle result=IntegrationStore.status(getContext(),pkg);
        result.putString("state","unsupported");result.putString("message","Unknown method");return result;
    }
    @Override public Cursor query(Uri uri,String[] projection,String selection,String[] args,String order){throw new UnsupportedOperationException("Use call");}
    @Override public String getType(Uri uri){return null;}
    @Override public Uri insert(Uri uri,ContentValues values){throw new UnsupportedOperationException("Use call");}
    @Override public int delete(Uri uri,String selection,String[] args){throw new UnsupportedOperationException("Use call");}
    @Override public int update(Uri uri,ContentValues values,String selection,String[] args){throw new UnsupportedOperationException("Use call");}
}
