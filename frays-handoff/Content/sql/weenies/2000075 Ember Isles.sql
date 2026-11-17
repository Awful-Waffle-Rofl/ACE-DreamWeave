-- @owner: frays
-- @owns: wcid:2000075
-- @requires-restart: no
--
-- Frays portal for the Drift Network: a copy of the retail Vissidal Island portal (32058) with MinLevel 300 and PortalRealm 1, so it lands on the Drift copy of Vissidal (realm 1), like the Drift Marae Lassel entry.

DELETE FROM `weenie` WHERE `class_Id` = 2000075;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000075, 'dw-vissidal-fray-portal', 7, '2026-10-30 00:07:46') /* Portal */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000075,   1,      65536) /* ItemType - Portal */
     , (2000075,  16,         32) /* ItemUseable - Remote */
     , (2000075,  86,        300) /* MinLevel */
     , (2000075,  93,       3084) /* PhysicsState - Ethereal, ReportCollisions, Gravity, LightingOn */
     , (2000075, 111,          1) /* PortalBitmask - Unrestricted */
     , (2000075, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000075, 9022,          1) /* PortalRealm */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000075,   1, True ) /* Stuck */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000075,  54,    -0.1) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000075,   1, 'Ember Isles') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000075,   1, 0x020005D3) /* Setup */
     , (2000075,   2, 0x09000003) /* MotionTable */
     , (2000075,   8, 0x0600106B) /* Icon */;

INSERT INTO `weenie_properties_position` (`object_Id`, `position_Type`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (2000075, 2, 0xD5DA003E, 179.999, 140.661, -0.445, 0.999954, 0, 0, 0.009607) /* Destination */
/* @teleloc 0xD5DA003E [179.998993 140.660995 -0.445000] 0.999954 0.000000 0.000000 0.009607 */;
