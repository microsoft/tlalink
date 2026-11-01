---- MODULE MetadataRoot ----
EXTENDS MetadataBase
VARIABLE own
RootApply(F(_), arg) == Apply(F, arg)
====
