---- MODULE Review ----
(* @link {"bind":[{"id":"review::commita","source":{"symbol":"M:InspectionExample.ReviewCounter.Adjust(System.Boolean)","syntax":"ExpressionStatement","tokens":"this.Commit(next);","match":1,"expected_matches":2}}]} *)
CommitA == TRUE
(* @link {"bind":[{"id":"review::commitb","source":{"symbol":"M:InspectionExample.ReviewCounter.Adjust(System.Boolean)","syntax":"ExpressionStatement","tokens":"this.Commit(next);","match":2,"expected_matches":2}}]} *)
CommitB == TRUE
(* @link {"bind":[{"id":"review::read","source":{"symbol":"M:InspectionExample.ReviewCounter.Adjust(System.Boolean)","syntax":"LocalDeclarationStatement","tokens":"int next = this.ReadNext();"}}]} *)
Read == TRUE
(* @link {"bind":[{"id":"review::release","source":{"symbol":"M:InspectionExample.ReviewCounter.Adjust(System.Boolean)","syntax":"ExpressionStatement","tokens":"this.Release();"}}]} *)
Release == TRUE
====
