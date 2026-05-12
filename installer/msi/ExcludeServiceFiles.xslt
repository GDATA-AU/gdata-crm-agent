<?xml version="1.0" encoding="utf-8"?>
<!--
  Heat post-processing transform.
  Removes components for files that are declared explicitly in Product.wxs
  (they carry ServiceInstall / ServiceControl and must not be duplicated).

  Heat emits a Component element AND a separate ComponentRef in the
  ComponentGroup.  We must suppress both, so we use an xsl:key to look up
  which component IDs were excluded by file name.
-->
<xsl:stylesheet version="1.0"
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:wix="http://wixtoolset.org/schemas/v4/wxs">

  <xsl:output method="xml" indent="yes" />

  <!-- Index of Component IDs to exclude, keyed by @Id -->
  <xsl:key name="ExcludedComponents"
    match="wix:Component[wix:File[
      contains(@Source, '\CrmAgent.exe')
      or contains(@Source, '\appsettings.json')
      or contains(@Source, '\appsettings.Development.json')
    ]]"
    use="@Id" />

  <!-- Identity: copy everything by default -->
  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <!-- Suppress Component elements for the three explicit files -->
  <xsl:template match="wix:Component[wix:File[
      contains(@Source, '\CrmAgent.exe')
      or contains(@Source, '\appsettings.json')
      or contains(@Source, '\appsettings.Development.json')
    ]]" />

  <!-- Suppress the matching ComponentRef entries in the ComponentGroup -->
  <xsl:template match="wix:ComponentRef[key('ExcludedComponents', @Id)]" />

</xsl:stylesheet>
