/****** Object:  StoredProcedure [dbo].[SP_PREMIUM_DUE_NOTIFICATION_JUVO_NEW_FOR_SECONDYRPAYPLN] ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- =============================================
-- Author:      Rekha
-- Create date: 19/06/2026
-- Description: Second-year paypln premium extraction list.
--              Feeds AutomatePayplnForSunLifeFor2yrs, which reads ONLY
--              'IBS #' and 'polcod'. Every other column was display data
--              for the notification mailer and has been removed.
-- EXEC SP_PREMIUM_DUE_NOTIFICATION_JUVO_NEW_FOR_SECONDYRPAYPLN 3458
-- =============================================
ALTER PROCEDURE [dbo].[SP_PREMIUM_DUE_NOTIFICATION_JUVO_NEW_FOR_SECONDYRPAYPLN]
  @actcod varchar(10)
AS
BEGIN
  SET NOCOUNT ON;

  SELECT DISTINCT
    a.polrefno 'IBS #',
    polcod     'polcod'
  FROM view_policy_details a
  JOIN paypln b
    ON a.polrefno = b.polrefno
    AND a.lcid = b.lcid
    AND a.compcode = b.compcode
  -- curmst and view_employee_list no longer supply any selected column, but
  -- they are INNER JOINs and therefore still act as filters: dropping them
  -- would let through policies with a currency missing from curmst, or with
  -- no matching servicing consultant. Kept so the row set is unchanged.
  JOIN curmst c
    ON a.polcur = c.curcod
  JOIN view_employee_list d
    ON a.ServicingConsultantCode = d.actcod
  -- Anti-join: skip anything already receipted for that year/month.
  -- Added by Nandan to avoid sending reminder if already paid.
  LEFT JOIN rcpdet e
    ON e.polrefno = a.polrefno
    AND e.yr = b.yr
    AND e.mon = b.mon
  WHERE e.polrefno IS NULL
    AND a.policm IN (@actcod) -- added by rekha 17/06/2025 for ticket #INC-26367
    AND CAST(b.poldue AS DATE)
          BETWEEN DATEADD(DD, 10, CAST(GETDATE() AS DATE))
              AND DATEADD(DD, 21, CAST(GETDATE() AS DATE))
    AND b.planyr = 2
    AND a.polsts IN ('ACT', 'PAR_SUR', 'PAID')
    AND a.polrefno NOT IN (
          SELECT polrefno FROM PolicyExclusionForNotification
          WHERE EffectiveTo IS NULL OR EffectiveTo <= GETDATE())
    AND polfreq <> 'M'
END
