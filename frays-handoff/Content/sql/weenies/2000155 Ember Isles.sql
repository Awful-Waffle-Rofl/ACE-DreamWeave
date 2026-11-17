-- @owner: frays
-- @owns: wcid:2000155
-- @requires-restart: no
--
-- Frays: the way back from the Eastern Heights arrival (0xD6E1 30, 116) to the temple, landing in front of 2000151.

DELETE FROM `weenie` WHERE `class_Id` = 2000155;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000155, 'dw-fray-east-return-portal', 7, '2026-11-02 18:04:03') /* Portal */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000155,   1,      65536) /* ItemType - Portal */
     , (2000155,  16,         32) /* ItemUseable - Remote */
     , (2000155,  86,        300) /* MinLevel */
     , (2000155,  93,       3084) /* PhysicsState - Ethereal, ReportCollisions, Gravity, LightingOn */
     , (2000155, 111,          1) /* PortalBitmask - Unrestricted */
     , (2000155, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000155, 9022,          1) /* PortalRealm */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000155,   1, True ) /* Stuck */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000155,  54,    -0.1) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000155,   1, 'Ember Isles') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000155,   1, 0x020005D5) /* Setup */
     , (2000155,   2, 0x09000003) /* MotionTable */
     , (2000155,   8, 0x0600106B) /* Icon */;

INSERT INTO `weenie_properties_position` (`object_Id`, `position_Type`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (2000155, 2, 0xD5DA003E, 179.999, 140.661, -0.445, 0.999954, 0, 0, 0.009607) /* Destination */
/* @teleloc 0xD5DA003E [179.998993 140.660995 -0.445000] 0.999954 0.000000 0.000000 0.009607 */;
